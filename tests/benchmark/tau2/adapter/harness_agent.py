"""把 AgentWorkbench 的 ModelInvocation 单步服务接成 τ²-bench 的 HalfDuplexAgent。

职责边界（**不要越界**）
------------------------
τ² 的 Orchestrator 持有**工具执行权**与**判分权**：它在 `step()` 里自己调
`environment.get_response(tool_call)` 改真实 DB，并在最后用 golden DB 状态打分。
因此本适配器**只做三件事**：

1. tau2 消息 → AgentWorkbench 协议 DTO；
2. HTTP 调一次 `/api/model/invoke`，拿回**一条** assistant 消息；
3. DTO → tau2 消息。

**不执行工具、不循环、不落库、不做轨迹合成。**

与 τ² 自带 LLMAgent 的保真度
----------------------------
system prompt 直接复用 τ² 自己的 `SYSTEM_PROMPT` / `AGENT_INSTRUCTION` 常量（不重写），
stop 判定与 `LLMAgent.is_stop` 一致（`###STOP###`）。这样"模型输出相同 ⇒ 行为相同"，
我们测到的是 harness 的差异，而不是适配器自己的怪癖。
"""

from __future__ import annotations

import json
import os
import urllib.error
import urllib.request
from typing import Any, Optional

from pydantic import BaseModel, ConfigDict, Field

from tau2.agent.base_agent import (
    AgentError,
    HalfDuplexAgent,
    ValidAgentInputMessage,
    is_valid_agent_history_message,
)
from tau2.agent.llm_agent import AGENT_INSTRUCTION, SYSTEM_PROMPT
from tau2.data_model.message import (
    AssistantMessage,
    Message,
    MultiToolMessage,
    SystemMessage,
    ToolCall,
    ToolMessage,
    UserMessage,
)
from tau2.environment.tool import Tool

STOP_TOKEN = "###STOP###"

AGENT_NAME = "agentworkbench"

DEFAULT_ENDPOINT = os.environ.get("AGENTWORKBENCH_ENDPOINT", "http://localhost:5000")


class ModelInvocationClient:
    """`/api/model/invoke` 的最小 HTTP 客户端（stdlib，无第三方依赖）。"""

    def __init__(self, endpoint: str = DEFAULT_ENDPOINT, timeout: float = 60.0):
        self.endpoint = endpoint.rstrip("/")
        self.timeout = timeout
        self.requests = 0

    def health(self) -> dict[str, Any]:
        with urllib.request.urlopen(f"{self.endpoint}/health", timeout=self.timeout) as resp:
            return json.loads(resp.read().decode("utf-8"))

    def invoke(
        self,
        messages: list[dict[str, Any]],
        tools: list[dict[str, Any]],
        model: Optional[str] = None,
    ) -> dict[str, Any]:
        payload = {"messages": messages, "tools": tools, "model": model}
        data = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        req = urllib.request.Request(
            f"{self.endpoint}/api/model/invoke", data=data, method="POST"
        )
        req.add_header("Content-Type", "application/json; charset=utf-8")
        self.requests += 1
        try:
            with urllib.request.urlopen(req, timeout=self.timeout) as resp:
                return json.loads(resp.read().decode("utf-8"))
        except urllib.error.HTTPError as e:
            body = e.read().decode("utf-8", "replace")
            raise AgentError(f"ModelInvocation HTTP {e.code}：{body}") from e
        except urllib.error.URLError as e:
            raise AgentError(f"ModelInvocation 不可达（{self.endpoint}）：{e}") from e


# --------------------------------------------------------------------------- #
# tau2 消息 → 协议 DTO
# --------------------------------------------------------------------------- #

def to_invoke_message(message: Message) -> dict[str, Any]:
    """把一条 tau2 消息转成 AgentWorkbench 的 InvokeMessage 形状。"""
    if isinstance(message, SystemMessage):
        return {"role": "system", "text": message.content, "toolCalls": None, "toolCallId": None}

    if isinstance(message, AssistantMessage):
        calls = None
        if message.tool_calls:
            calls = [
                {
                    "id": call.id,
                    "name": call.name,
                    # 协议里 arguments 是 JSON 字符串（原样透传），不是对象。
                    "arguments": json.dumps(call.arguments, ensure_ascii=False),
                }
                for call in message.tool_calls
            ]
        return {
            "role": "assistant",
            "text": message.content or None,
            "toolCalls": calls,
            "toolCallId": None,
        }

    if isinstance(message, ToolMessage):
        # tool 结果必须带 tool_call_id，否则供应商侧无法关联到上一次调用。
        return {
            "role": "tool",
            "text": message.content,
            "toolCalls": None,
            "toolCallId": message.id,
        }

    if isinstance(message, UserMessage):
        if message.is_tool_call():
            # retail 域没有 user 工具（已 dump 确认）。真出现时宁可炸，也不要静默丢调用。
            raise AgentError("用户侧工具调用暂不支持（retail 域不存在用户工具）")
        return {"role": "user", "text": message.content, "toolCalls": None, "toolCallId": None}

    raise AgentError(f"不支持的消息类型：{type(message).__name__}")


# --------------------------------------------------------------------------- #
# 协议 DTO → tau2 消息
# --------------------------------------------------------------------------- #

def from_invoke_message(payload: dict[str, Any]) -> AssistantMessage:
    """把 InvokeResponse 转成**一条** tau2 assistant 消息。

    τ² 约定（`check_communication_error`，validate_communication=True 时强制）：
    一条消息要么是文本（发给用户），要么是 tool_calls（发给环境），**不能兼有、不能全空**。
    模型偶发「文本 + tool_calls 并存」时，这里**丢弃伴随文本、保留 tool_calls**——
    与 τ² 自带 LLMAgent 的实际路由一致（`is_tool_call()` 优先走 ENV）。
    """
    message = payload.get("message") or {}
    raw_calls = message.get("toolCalls") or []
    text = message.get("text")

    if raw_calls:
        calls: list[ToolCall] = []
        for raw in raw_calls:
            arguments = raw.get("arguments") or "{}"
            if isinstance(arguments, str):
                try:
                    arguments = json.loads(arguments)
                except json.JSONDecodeError:
                    arguments = {}
            calls.append(
                ToolCall(
                    id=raw.get("id") or "",
                    name=raw["name"],
                    arguments=arguments,
                    requestor="assistant",
                )
            )
        return AssistantMessage(role="assistant", content=None, tool_calls=calls, cost=0.0)

    if text is None or not text.strip():
        # 空消息必被 τ² 判 AGENT_ERROR → 0 分。与其让它在下游变成一句含糊的协议错误，
        # 不如在这里 fail-loud 说清楚是谁的问题。
        raise AgentError(
            "模型返回了空消息（既无文本也无 tool_calls）。τ² 会判 AGENT_ERROR 直接 0 分；"
            f"原始响应：{json.dumps(payload, ensure_ascii=False)[:400]}"
        )

    return AssistantMessage.text(text, cost=0.0)


# --------------------------------------------------------------------------- #
# Agent
# --------------------------------------------------------------------------- #

class HarnessAgentState(BaseModel):
    """适配器自身的历史。tau2 每轮**只喂最新一条**消息，历史得自己攒。"""

    model_config = ConfigDict(arbitrary_types_allowed=True)

    messages: list[Message] = Field(default_factory=list)


class HarnessAgent(HalfDuplexAgent[HarnessAgentState]):
    """把每一轮交给 AgentWorkbench 的模型调用链去回答。"""

    STOP_TOKEN = STOP_TOKEN

    def __init__(
        self,
        tools: list[Tool],
        domain_policy: str,
        endpoint: str = DEFAULT_ENDPOINT,
        model: Optional[str] = None,
        timeout: float = 60.0,
    ):
        super().__init__(tools=tools, domain_policy=domain_policy)
        # 与 τ² 自带 LLMAgent 完全相同的 system prompt（复用它的常量，不自己编）。
        self.system_prompt = SYSTEM_PROMPT.format(
            domain_policy=domain_policy, agent_instruction=AGENT_INSTRUCTION
        )
        self.tool_schemas = [self._declare(tool) for tool in tools]
        self.client = ModelInvocationClient(endpoint, timeout)
        self.model = model

    @staticmethod
    def _declare(tool: Tool) -> dict[str, Any]:
        """工具的**纯声明**（schema，无执行体）——执行权在 τ² 的 Orchestrator。"""
        schema = tool.openai_schema
        fn = schema["function"]
        return {
            "name": fn["name"],
            "description": fn.get("description"),
            "parameters": fn.get("parameters"),
        }

    def get_init_state(
        self, message_history: Optional[list[Message]] = None
    ) -> HarnessAgentState:
        history = list(message_history or [])
        assert all(is_valid_agent_history_message(m) for m in history), (
            "history 只能含 AssistantMessage / UserMessage(非工具调用) / ToolMessage(来自 assistant)"
        )
        return HarnessAgentState(messages=history)

    def generate_next_message(
        self, message: Optional[ValidAgentInputMessage], state: HarnessAgentState
    ) -> tuple[AssistantMessage, HarnessAgentState]:
        if isinstance(message, MultiToolMessage):
            # 一条 assistant 消息带多个 tool_call 时，τ² 把结果包成一个 MultiToolMessage。
            state.messages.extend(message.tool_messages)
        elif message is not None:
            state.messages.append(message)

        request = [to_invoke_message(SystemMessage(role="system", content=self.system_prompt))]
        request.extend(to_invoke_message(m) for m in state.messages)

        response = self.client.invoke(request, self.tool_schemas, self.model)
        assistant = from_invoke_message(response)

        state.messages.append(assistant)
        return assistant, state

    @classmethod
    def is_stop(cls, message: AssistantMessage) -> bool:
        """与 `LLMAgent.is_stop` 一致：文本里出现 ###STOP### 即终止。"""
        if message.content is None:
            return False
        return cls.STOP_TOKEN in message.content


def create_harness_agent(tools: list[Tool], domain_policy: str, **kwargs: Any) -> HarnessAgent:
    """registry factory（签名遵循 τ² 约定：`factory(tools, domain_policy, **kwargs)`）。"""
    return HarnessAgent(
        tools=tools,
        domain_policy=domain_policy,
        endpoint=kwargs.get("endpoint") or DEFAULT_ENDPOINT,
        model=kwargs.get("model"),
        timeout=float(kwargs.get("timeout") or 60.0),
    )

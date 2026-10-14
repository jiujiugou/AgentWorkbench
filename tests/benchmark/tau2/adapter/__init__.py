"""τ²-bench ⇄ AgentWorkbench 适配器包。

- `harness_agent`：把 .NET 的 ModelInvocation 单步服务包成 τ² 的 HalfDuplexAgent。
- `scripted_user`：确定性 user（无 LLM user simulator，也不需要 API key）。
- `register_all()`：在**运行时**向 τ² registry 注册二者（不改 τ² 源码）。
"""

from .harness_agent import (
    AGENT_NAME,
    STOP_TOKEN,
    HarnessAgent,
    HarnessAgentState,
    ModelInvocationClient,
    create_harness_agent,
    from_invoke_message,
    to_invoke_message,
)
from .scripted_user import USER_NAME, ScriptedUser, create_scripted_user

__all__ = [
    "AGENT_NAME",
    "USER_NAME",
    "STOP_TOKEN",
    "HarnessAgent",
    "HarnessAgentState",
    "ModelInvocationClient",
    "ScriptedUser",
    "create_harness_agent",
    "create_scripted_user",
    "from_invoke_message",
    "to_invoke_message",
    "register_all",
]


def register_all() -> list[str]:
    """向 τ² 的全局 registry 注册适配器。

    τ² 官方扩展点（src/tau2/agent/AGENTS.md）：agent 由「factory + register_agent_factory」
    注册。**我们不修改 D:\\Code\\tau2-bench 的任何文件**，改为在运行时注册——因此本函数
    必须在任何 `registry.get_agent_factory(...)` / CLI 解析之前调用。

    幂等：重复调用不会抛（registry 对重名会 raise，这里先查再注册）。
    返回本次**实际新增**的注册名。
    """
    from tau2.registry import registry

    added: list[str] = []
    if registry.get_agent_factory(AGENT_NAME) is None:
        registry.register_agent_factory(
            create_harness_agent,
            AGENT_NAME,
            metadata={"agentworkbench": True, "transport": "http:/api/model/invoke"},
        )
        added.append(AGENT_NAME)

    if USER_NAME not in registry.get_users():
        registry.register_user(ScriptedUser, USER_NAME)
        added.append(USER_NAME)

    return added

"""确定性 user：不依赖 LLM user simulator，也不需要任何 API key。

**诚实边界**：τ² 官方的 user 是 LLM user simulator（要 key）。本类用「脚本化 user」替代，
因此它**只能验证链路是否打通**，不能代表真实的用户行为分布——真实评测必须换回
`user_simulator`（需要 key）或把 user 侧也接到 MockLlm 上。

终止判定不由本类决定：orchestrator 硬编码调用 `UserSimulator.is_stop(...)`，
所以只要消息文本含 `###STOP###` 就会被识别为 USER_STOP。
"""

from __future__ import annotations

from typing import Any, Optional

from tau2.data_model.message import AssistantMessage, Message, ToolMessage, UserMessage
from tau2.user.user_simulator_base import HalfDuplexUser, UserState

from .harness_agent import STOP_TOKEN

USER_NAME = "scripted_user"

DEFAULT_OPENING = "你好，我有一个订单需要处理，麻烦帮我看看。"


class ScriptedUser(HalfDuplexUser[UserState]):
    """第 1 轮说一句开场，第 2 轮起一律发 ###STOP###。"""

    STOP_TOKEN = STOP_TOKEN

    def __init__(self, opening: str = DEFAULT_OPENING):
        super().__init__(instructions=None, tools=None)
        self.opening = opening
        self._turn = 0

    def get_init_state(
        self, message_history: Optional[list[Message]] = None
    ) -> UserState:
        return UserState(system_messages=[], messages=list(message_history or []))

    def generate_next_message(
        self,
        message: Optional[AssistantMessage | ToolMessage],
        state: UserState,
    ) -> tuple[UserMessage, UserState]:
        self._turn += 1
        text = self.opening if self._turn == 1 else self.STOP_TOKEN
        reply = UserMessage.text(text)
        state.messages.append(reply)
        return reply, state

    @classmethod
    def is_stop(cls, message: UserMessage) -> bool:
        if message.content is None:
            return False
        return cls.STOP_TOKEN in message.content


def create_scripted_user(opening: str = DEFAULT_OPENING, **_: Any) -> ScriptedUser:
    """registry user factory。"""
    return ScriptedUser(opening=opening)

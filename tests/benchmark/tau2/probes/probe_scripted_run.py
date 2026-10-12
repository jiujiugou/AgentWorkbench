#!/usr/bin/env python3
"""
V1+V2 Probe: Scripted end-to-end run for retail task 73.
V1: Verifies reward == 1.0 with EvaluationType.ENV using scripted agent + user (no LLM).
V2: Verifies strict_replay=True doesn't crash.
"""

from __future__ import annotations

import json
import uuid
from typing import Any, Optional

from tau2.agent.base_agent import HalfDuplexAgent
from tau2.data_model.message import (
    AssistantMessage,
    ToolCall,
    ToolMessage,
    UserMessage,
)
from tau2.data_model.simulation import SimulationRun, TerminationReason
from tau2.data_model.tasks import Task
from tau2.environment.environment import Environment
from tau2.evaluator.evaluator import EvaluationType, evaluate_simulation
from tau2.orchestrator.orchestrator import Orchestrator
from tau2.registry import registry
from tau2.user.user_simulator_base import HalfDuplexUser, UserState


class ScriptedAgent(HalfDuplexAgent[None]):
    """Agent that sends the golden action tool_call on first turn, then stops."""

    def __init__(self, tools: list, domain_policy: str):
        super().__init__(tools, domain_policy)
        self._turn = 0

    def get_init_state(
        self, message_history: Optional[list] = None
    ) -> None:
        return None

    def generate_next_message(
        self,
        message: UserMessage | ToolMessage,
        state: None,
    ) -> tuple[AssistantMessage, None]:
        self._turn += 1
        if self._turn == 1:
            # First turn: send the golden action tool call
            tool_call = ToolCall(
                id=str(uuid.uuid4()),
                name="return_delivered_order_items",
                arguments={
                    "order_id": "#W5272531",
                    "item_ids": [
                        "7228247242",
                        "2698416822",
                        "8098621301",
                        "3320557165",
                    ],
                    "payment_method_id": "credit_card_6824399",
                },
                requestor="assistant",
            )
            return AssistantMessage.text("", tool_calls=[tool_call]), None
        else:
            # After receiving tool result, send STOP
            return AssistantMessage.text("###STOP###"), None

    def is_stop(self, message: AssistantMessage) -> bool:
        return message.has_text_content() and "###STOP###" in (message.content or "")


class ScriptedUser(HalfDuplexUser[UserState]):
    """User that responds once then sends STOP."""

    def __init__(self):
        super().__init__(instructions=None, tools=None)
        self._turn = 0

    def get_init_state(
        self, message_history: Optional[list] = None
    ) -> UserState:
        return UserState(system_messages=[], messages=[])

    def generate_next_message(
        self,
        message: AssistantMessage | ToolMessage,
        state: UserState,
    ) -> tuple[UserMessage, UserState]:
        self._turn += 1
        if self._turn == 1:
            # First response: polite acknowledgment
            return UserMessage.text(
                "Thank you for processing my return. I appreciate your help."
            ), state
        else:
            # Then STOP
            return UserMessage.text("###STOP###"), state


def main() -> int:
    # 1. Load task 73
    loader = registry.get_tasks_loader("retail")
    tasks = loader("base")
    task73 = next(t for t in tasks if str(t.id) == "73")
    print(f"Loaded task 73: {task73.id}")
    print(f"  reward_basis: {task73.evaluation_criteria.reward_basis}")
    print(f"  actions: {task73.evaluation_criteria.actions}")

    # 2. Create environment
    env = registry.get_env_constructor("retail")()

    # 3. Create scripted agent and user
    agent = ScriptedAgent(env.get_tools(), env.get_policy())
    user = ScriptedUser()

    # 4. Create orchestrator
    orchestrator = Orchestrator(
        domain="retail",
        agent=agent,
        user=user,
        environment=env,
        task=task73,
        max_steps=10,
        max_errors=5,
        seed=42,
        validate_communication=False,
    )

    # 5. Run simulation
    print("\nRunning simulation...")
    sim_run: SimulationRun = orchestrator.run()

    # 6. Print trajectory
    print(f"\nTermination reason: {sim_run.termination_reason}")
    print(f"Steps: {orchestrator.step_count}")
    print(f"Messages in trajectory: {len(sim_run.messages)}")
    print("\n--- TRAJECTORY ---")
    for i, msg in enumerate(sim_run.messages):
        print(f"[{i}] {msg}")

    # 7. Evaluate with ENV (DB check only)
    print("\n--- EVALUATION (ENV) ---")
    reward_info = evaluate_simulation(
        simulation=sim_run,
        task=task73,
        evaluation_type=EvaluationType.ENV,
        solo_mode=False,
        domain="retail",
        strict_replay=True,
    )
    print(f"Reward: {reward_info.reward}")
    print(f"Reward basis: {reward_info.reward_basis}")
    print(f"DB check: {reward_info.db_check}")
    print(f"Info: {reward_info.info}")

    # 8. Negative control: wrong order_id
    print("\n--- NEGATIVE CONTROL (wrong order_id) ---")
    return run_negative_control(task73, env)


def run_negative_control(task73: Task, env: Environment) -> int:
    """Run with wrong order_id to verify reward == 0.0."""

    class BadAgent(HalfDuplexAgent[None]):
        def __init__(self, tools: list, domain_policy: str):
            super().__init__(tools, domain_policy)
            self._turn = 0

        def get_init_state(self, message_history=None):
            return None

        def generate_next_message(self, message, state):
            self._turn += 1
            if self._turn == 1:
                tool_call = ToolCall(
                    id=str(uuid.uuid4()),
                    name="return_delivered_order_items",
                    arguments={
                        "order_id": "#WRONG_ORDER_ID",
                        "item_ids": [
                            "7228247242",
                            "2698416822",
                            "8098621301",
                            "3320557165",
                        ],
                        "payment_method_id": "credit_card_6824399",
                    },
                    requestor="assistant",
                )
                return AssistantMessage.text("", tool_calls=[tool_call]), None
            else:
                return AssistantMessage.text("###STOP###"), None

        def is_stop(self, message):
            return message.has_text_content() and "###STOP###" in (message.content or "")

    agent = BadAgent(env.get_tools(), env.get_policy())
    user = ScriptedUser()

    orchestrator = Orchestrator(
        domain="retail",
        agent=agent,
        user=user,
        environment=env,
        task=task73,
        max_steps=10,
        max_errors=5,
        seed=42,
        validate_communication=False,
    )

    sim_run = orchestrator.run()
    print(f"Termination reason: {sim_run.termination_reason}")

    reward_info = evaluate_simulation(
        simulation=sim_run,
        task=task73,
        evaluation_type=EvaluationType.ENV,
        solo_mode=False,
        domain="retail",
        strict_replay=True,
    )
    print(f"Reward: {reward_info.reward}")
    print(f"DB check: {reward_info.db_check}")

    # Verify negative control
    if reward_info.reward == 0.0:
        print("\n[PASS] NEGATIVE CONTROL PASSED: reward == 0.0 with wrong order_id")
    else:
        print(f"\n[FAIL] NEGATIVE CONTROL FAILED: expected 0.0, got {reward_info.reward}")

    # V2: Test strict_replay
    print("\n=== V2: Testing strict_replay ===")
    test_strict_replay(task73, env)

    return 0


def test_strict_replay(task73: Task, env: Environment) -> None:
    """Test that strict_replay=True doesn't crash."""
    agent = ScriptedAgent(env.get_tools(), env.get_policy())
    user = ScriptedUser()

    orchestrator = Orchestrator(
        domain="retail",
        agent=agent,
        user=user,
        environment=env,
        task=task73,
        max_steps=10,
        max_errors=5,
        seed=42,
        validate_communication=False,
    )

    sim_run = orchestrator.run()
    print(f"Simulation done, termination: {sim_run.termination_reason}")

    # Test strict_replay=True
    print("Running evaluate_simulation with strict_replay=True...")
    try:
        reward_info_strict = evaluate_simulation(
            simulation=sim_run,
            task=task73,
            evaluation_type=EvaluationType.ENV,
            solo_mode=False,
            domain="retail",
            strict_replay=True,
        )
        print(f"  strict_replay=True: reward={reward_info_strict.reward}, db_match={reward_info_strict.db_check.db_match if reward_info_strict.db_check else 'N/A'}")
    except Exception as e:
        print(f"  strict_replay=True FAILED: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()

    # Test strict_replay=False for comparison
    print("Running evaluate_simulation with strict_replay=False...")
    try:
        reward_info_nostrict = evaluate_simulation(
            simulation=sim_run,
            task=task73,
            evaluation_type=EvaluationType.ENV,
            solo_mode=False,
            domain="retail",
            strict_replay=False,
        )
        print(f"  strict_replay=False: reward={reward_info_nostrict.reward}, db_match={reward_info_nostrict.db_check.db_match if reward_info_nostrict.db_check else 'N/A'}")
    except Exception as e:
        print(f"  strict_replay=False FAILED: {type(e).__name__}: {e}")
        import traceback
        traceback.print_exc()


if __name__ == "__main__":
    exit(main())
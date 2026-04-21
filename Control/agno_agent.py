from typing import Optional, Literal, List, Dict, Any
from pydantic import BaseModel, Field
import logging
import json
import re

from agno.agent import Agent
from agno.tools import tool, Toolkit
from agno.tools.reasoning import ReasoningTools

# Optional model helper (defer import to runtime)
try:
    OpenRouter = None
    from agno.models.openrouter import OpenRouter
except Exception:
    OpenRouter = None

logger = logging.getLogger("agno_drone")
logging.basicConfig(level=logging.INFO)


# -----------------------------
# Command Schemas (Structured Output)
# -----------------------------

class CmdTakeoff(BaseModel):
    alt: float = 10.0


class CmdArm(BaseModel):
    arm: bool


class CmdSetMode(BaseModel):
    mode: Literal["GUIDED", "ALT_HOLD", "RTL", "AUTO"]


class CmdGoToCoords(BaseModel):
    lat: float
    lon: float
    alt: Optional[float] = None
    frame: Literal["Relative", "Global"] = "Relative"


class CmdSetHeading(BaseModel):
    yaw: float
    frame: Literal["Relative", "Global"] = "Relative"


# -----------------------------
# Modern Drone Toolkit (modern agno pattern)
# -----------------------------

class DroneToolkit(Toolkit):
    def __init__(self, vehicle):
        super().__init__(name="drone_control")
        self.vehicle = vehicle
        # Register tools explicitly to be explicit about their availability
        try:
            self.register(self.arm)
            self.register(self.takeoff)
            self.register(self.set_mode)
            self.register(self.goto_coords)
            self.register(self.set_heading)
        except Exception:
            # Some Toolkit implementations auto-register decorated methods
            pass

    @tool
    def arm(self, arm: bool) -> dict:
        self.vehicle.armed = arm
        return {"status": "armed" if arm else "disarmed"}

    @tool
    def takeoff(self, alt: float) -> dict:
        if not getattr(self.vehicle, "armed", False):
            self.vehicle.armed = True
        self.vehicle.simple_takeoff(alt)
        return {"status": f"taking off to {alt}m"}

    @tool
    def set_mode(self, mode: Literal["GUIDED", "ALT_HOLD", "RTL", "AUTO"]) -> dict:
        from dronekit import VehicleMode
        self.vehicle.mode = VehicleMode(mode)
        return {"status": f"mode set to {mode}"}

    @tool
    def goto_coords(self, lat: float, lon: float, alt: float, frame: Literal["Relative", "Global"] = "Relative") -> dict:
        from dronekit import LocationGlobal, LocationGlobalRelative
        dest = LocationGlobalRelative(lat, lon, alt) if frame == "Relative" else LocationGlobal(lat, lon, alt)
        self.vehicle.simple_goto(dest)
        return {"status": "moving to coordinates"}

    @tool
    def set_heading(self, yaw: float, frame: Literal["Relative", "Global"] = "Relative") -> dict:
        from pymavlink import mavutil
        is_relative = 1 if frame == "Relative" else 0
        msg = self.vehicle.message_factory.command_long_encode(
            0, 0, mavutil.mavlink.MAV_CMD_CONDITION_YAW, 0,
            yaw, 0, 1, is_relative, 0, 0, 0
        )
        self.vehicle.send_mavlink(msg)
        return {"status": f"heading set to {yaw}"}



# -----------------------------
# Execution Helper
# -----------------------------

def execute_structured_commands(commands: List[Dict[str, Any]], drone):
    results = []

    for cmd in commands:
        try:
            if "cmd_Arm" in cmd:
                results.append(drone.arm(**cmd["cmd_Arm"]))

            elif "cmd_Takeoff" in cmd:
                # safety: ensure armed
                drone.arm(True)
                results.append(drone.takeoff(**cmd["cmd_Takeoff"]))

            elif "cmd_SetMode" in cmd:
                results.append(drone.set_mode(**cmd["cmd_SetMode"]))

            elif "cmd_GoToCoords" in cmd:
                results.append(drone.goto_coords(**cmd["cmd_GoToCoords"]))

            elif "cmd_SetHeading" in cmd:
                results.append(drone.set_heading(**cmd["cmd_SetHeading"]))

            else:
                results.append({"error": "unknown command", "cmd": cmd})

        except Exception as e:
            logger.exception("Execution failed")
            results.append({"error": str(e), "cmd": cmd})

    return results


# -----------------------------
# Agent Factory
# -----------------------------

def create_drone_agent(vehicle):
    """Create and return a modern Agno `Agent` wired to the drone toolkit.

    The returned object is the Agno `Agent` instance; callers should use
    `agent.run(prompt)` to execute prompts.
    """
    drone_toolkit = DroneToolkit(vehicle)

    tools = [drone_toolkit]
    if ReasoningTools is not None:
        tools.append(ReasoningTools())

    agent_kwargs = dict(
        name="Drone Agent",
        description="I am a flight controller that converts natural language into drone actions.",
        tools=tools,
        instructions=[
            "You are a drone pilot.",
            "When a user gives a flight command, use the appropriate tool to execute it.",
            "Always ensure the drone is armed before attempting takeoff.",
            "Summarize the actions taken after execution."
        ]
    )

    if OpenRouter is not None:
        try:
            agent_kwargs["model"] = OpenRouter(id="gpt-5-mini")
        except Exception:
            pass

    agent = Agent(**agent_kwargs)

    return agent


def execute_commands(commands, vehicle, blocking=True):
    """
    Execute structured commands (list or single dict) against a DroneKit vehicle.

    - `commands` can be a list of command dicts or a single dict.
    - `vehicle` is the DroneKit vehicle instance.
    - `blocking` is accepted for compatibility (server runs this in a thread).
    Returns the execution results list.
    """
    if vehicle is None:
        raise ValueError("vehicle is None")

    # Normalize commands into a list
    cmds = []
    if isinstance(commands, dict):
        cmds = [commands]
    elif isinstance(commands, (list, tuple)):
        cmds = list(commands)
    else:
        # Try to parse if it's a JSON string
        try:
            parsed = json.loads(str(commands))
            cmds = parsed if isinstance(parsed, list) else [parsed]
        except Exception:
            raise ValueError("Unsupported commands format")

    drone_tool = DroneToolkit(vehicle)
    results = execute_structured_commands(cmds, drone_tool)
    return results


__all__ = ["create_drone_agent", "execute_commands"]
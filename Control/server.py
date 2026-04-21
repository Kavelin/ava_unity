
import asyncio
import math
from pathlib import Path
from typing import Set
import mimetypes

from fastapi import FastAPI, WebSocket, WebSocketDisconnect, Request, Response
from fastapi.staticfiles import StaticFiles
from fastapi.responses import FileResponse, JSONResponse
import uvicorn
import re

# dronekit patch
try:
    import collections
    if not hasattr(collections, 'MutableMapping'):
        import collections.abc as _collections_abc
        collections.MutableMapping = _collections_abc.MutableMapping
except Exception:
    pass

from dronekit import connect

DRONEKIT_CONNECTION_STRING = 'tcp:127.0.0.1:5763'
DRONEKIT_BAUD = 57600
DRONEKIT_RATE = 60
WEBSOCKET_BROADCAST_INTERVAL = 1 / 60 # 60hz seems to be stable
BUILD_PATH = Path(__file__).parent.parent / "Build"
vehicle = None
agent_runner = None
connected_websockets: Set[WebSocket] = set()
vehicle_data_lock = asyncio.Lock()
latest_vehicle_data = {}

def vehicle_to_dict(vehicle, z_invert=True, scale=100):
    """
    Converts vehicle data to a dictionary for transmission.
    :param vehicle: The vehicle object from dronekit.
    :param z_invert: Invert the Z axis for local frame (default is True because Ardupilot uses NED).
    :param scale: The scale of the world (default is 100, UE uses cm).
    """
    # Provide defaults in case attributes are missing
    d = { 
        "lat": None, "lon": None, "alt": None,
        "n": 0.0, "e": 0.0, "d": 0.0,
        "roll": 0.0, "pitch": 0.0, "yaw": 0.0,
    }
    try:
        gf = getattr(vehicle, "location", None)
        gf = getattr(gf, "global_frame", None) if gf is not None else None
        if gf is not None:
            d["lat"] = getattr(gf, "lat", None)
            d["lon"] = getattr(gf, "lon", None)
            d["alt"] = getattr(gf, "alt", None)

        lf = getattr(vehicle.location, "local_frame", None) if getattr(vehicle, "location", None) is not None else None
        if lf is not None:
            n = getattr(lf, "north", None)
            e = getattr(lf, "east", None)
            down = getattr(lf, "down", None)
            d["n"] = (n * scale) if isinstance(n, (int, float)) else 0.0
            d["e"] = (e * scale) if isinstance(e, (int, float)) else 0.0
            d["d"] = (down * scale) if isinstance(down, (int, float)) else 0.0

        if z_invert and isinstance(d["d"], (int, float)):
            d["d"] *= -1

        att = getattr(vehicle, "attitude", None)
        if att is not None:
            r = getattr(att, "roll", None)
            p = getattr(att, "pitch", None)
            y = getattr(att, "yaw", None)
            d["roll"] = round(math.degrees(r), 3) if isinstance(r, (int, float)) else 0.0
            d["pitch"] = round(math.degrees(p), 3) if isinstance(p, (int, float)) else 0.0
            d["yaw"] = round(math.degrees(y), 3) if isinstance(y, (int, float)) else 0.0

        if isinstance(d["lat"], float):
            d["lat"] = round(d["lat"], 8)
        if isinstance(d["lon"], float):
            d["lon"] = round(d["lon"], 8)
        if isinstance(d["alt"], float):
            d["alt"] = round(d["alt"], 8)

    except Exception as e:
        print(f"vehicle_to_dict error: {e}")

    return d


def create_fields_string(fields_list):
    """
    Creates a string of fields separated by spaces (from tcp_relay.py).
    :param fields_list: List of fields to be included in the string.
    :return: String of fields separated by spaces.
    """
    field_str = "{} " * len(fields_list)
    return field_str.format(*fields_list).rstrip()


def vehicle_data_to_fields(vehicle_data):
    """
    Converts vehicle data dictionary to a fields list for transmission.
    """
    fields = [0.0] * 23

    # Safely pull values with defaults
    fields[0] = float(vehicle_data.get("n", 0.0) or 0.0)
    fields[1] = float(vehicle_data.get("e", 0.0) or 0.0)
    fields[2] = float(vehicle_data.get("d", 0.0) or 0.0)
    fields[3] = float(vehicle_data.get("roll", 0.0) or 0.0)
    fields[4] = float(vehicle_data.get("pitch", 0.0) or 0.0)
    fields[5] = float(vehicle_data.get("yaw", 0.0) or 0.0)

    # Mount0
    fields[6] = 0.0
    fields[7] = 0.0
    fields[8] = 0.0

    # Mount1
    fields[9] = 0.0
    fields[10] = 0.0
    fields[11] = 0.0

    fields[12] = 0
    fields[13] = 80.0

    # Remaining fields kept at 0.0
    return fields

async def dronekit_connection_task():
    """
    Background task that connects to DroneKit and continuously reads vehicle data.
    """
    global vehicle, latest_vehicle_data, agent_runner

    try:
        print(f"Connecting to vehicle at {DRONEKIT_CONNECTION_STRING}...")
        vehicle = connect(
            DRONEKIT_CONNECTION_STRING,
            wait_ready=True,
            baud=DRONEKIT_BAUD,
            rate=DRONEKIT_RATE
        )
        print("Vehicle connected.")

        # 🔥 CREATE AGENT HERE
        from agno_agent import create_drone_agent
        agent_runner = create_drone_agent(vehicle)
        print("Agno agent initialized.")

        def _has_local_frame(v):
            try:
                lf = getattr(v.location, "local_frame", None)
                return lf is not None and getattr(lf, "north", None) is not None
            except Exception:
                return False

        while not _has_local_frame(vehicle):
            await asyncio.sleep(1)
            print("Waiting for location.local_frame...")

        print("Location ready.")

        while True:
            try:
                if vehicle:
                    data = vehicle_to_dict(vehicle)
                    async with vehicle_data_lock:
                        latest_vehicle_data = data
                await asyncio.sleep(1 / DRONEKIT_RATE)
            except Exception as e:
                print(f"Error reading vehicle data: {e}")
                await asyncio.sleep(0.1)

    except Exception as e:
        print(f"ERROR: Failed to connect to vehicle: {e}")


app = FastAPI(title="AVA Drone WebSocket Server")

# Ensure `.wasm` files are served with the correct MIME type
mimetypes.add_type("application/wasm", ".wasm")


@app.middleware("http")
async def ensure_wasm_headers(request: Request, call_next):
    """Strip incorrect Content-Encoding headers for .wasm responses and
    ensure the Content-Type is `application/wasm`.
    This addresses browsers failing to instantiate streaming-compiled wasm
    when the server adds a Content-Encoding header but the file is not
    actually pre-compressed (or vice-versa).
    """
    response = await call_next(request)
    try:
        path = request.url.path
        if path.endswith('.wasm'):
            # Remove potentially incorrect encoding header
            if 'content-encoding' in response.headers:
                del response.headers['content-encoding']
            # Force correct MIME type for wasm
            response.headers['content-type'] = 'application/wasm'
    except Exception:
        pass
    return response


@app.on_event("startup")
async def startup_event():
    """Start the DroneKit connection task on server startup."""
    asyncio.create_task(dronekit_connection_task())


@app.get("/")
async def root():
    """Serve index.html from the WebGL build."""
    index_path = BUILD_PATH / "index.html"
    if index_path.exists():
        return FileResponse(index_path)
    return {"message": "WebGL build not found at " + str(BUILD_PATH)}


async def _handle_ai_prompt(prompt: str, websocket: WebSocket | None = None):
    global agent_runner

    if agent_runner is None:
        print("Agent not ready yet.")
        return

    try:
        # Run agent in thread (LLM call is blocking)
        fn = getattr(agent_runner, "run", agent_runner)
        result = await asyncio.to_thread(fn, prompt)

        print("Agent result:", result)

        # Extract the user-facing content from the agent result
        def _extract_agent_content(res):
            if res is None:
                return None
            # primitives
            if isinstance(res, (str, int, float, bool)):
                return res
            # dict preference order
            if isinstance(res, dict):
                for k in ("content", "data", "text", "result", "output", "commands", "results"):
                    if k in res:
                        return res[k]
                return res
            # objects with attributes
            for attr in ("content", "text", "result", "output", "data"):
                if hasattr(res, attr):
                    try:
                        return getattr(res, attr)
                    except Exception:
                        continue
            # try __dict__
            try:
                d = getattr(res, "__dict__", None)
                if isinstance(d, dict):
                    for k in ("content", "data", "text", "result", "output", "commands", "results"):
                        if k in d:
                            return d[k]
            except Exception:
                pass
            return str(res)

        payload = _extract_agent_content(result)

        if websocket:
            # Ensure JSON serializable payload
            try:
                await websocket.send_json({"type": "agent_result", "data": payload})
            except Exception:
                await websocket.send_json({"type": "agent_result", "data": str(payload)})

    except Exception as e:
        print(f"Agent execution error: {e}")


async def _broadcast_telemetry(websocket: WebSocket):
    """Background task: continuously broadcast vehicle telemetry."""
    try:
        while True:
            async with vehicle_data_lock:
                if latest_vehicle_data:
                    fields = vehicle_data_to_fields(latest_vehicle_data)
                    message_str = create_fields_string(fields)
                    try:
                        await websocket.send_text(message_str)
                    except Exception as e:
                        print(f"Error sending telemetry: {e}")
                        raise
            await asyncio.sleep(WEBSOCKET_BROADCAST_INTERVAL)
    except asyncio.CancelledError:
        pass
    except Exception as e:
        print(f"Telemetry broadcast error: {e}")
        raise


async def _receive_commands(websocket: WebSocket):
    """Background task: receive commands from client."""
    try:
        while True:
            try:
                message = await asyncio.wait_for(websocket.receive_text(), timeout=60)
                if message and not message.isspace():
                    print(f"Received from client: {message}")

                    # Try JSON first (legacy clients sending structured commands)
                    parsed = None
                    try:
                        import json
                        parsed = json.loads(message)
                    except Exception:
                        parsed = None

                    if isinstance(parsed, (dict, list)):
                        # Legacy JSON command(s)
                        try:
                            from agno_agent import execute_commands
                            if vehicle is None:
                                print("No vehicle connected; cannot execute commands right now.")
                                continue
                            await asyncio.to_thread(execute_commands, parsed, vehicle, True)
                        except Exception as e:
                            print(f"Error executing JSON commands: {e}")
                    else:
                        # Treat as plain AI response text and handle it
                        await _handle_ai_prompt(message, websocket)
            except asyncio.TimeoutError:
                # No message within timeout, continue listening
                continue
            except Exception as e:
                raise
    except asyncio.CancelledError:
        pass
    except Exception as e:
        print(f"Command receive error: {e}")
        raise


@app.websocket("/ws")
async def websocket_endpoint(websocket: WebSocket):
    """
    WebSocket endpoint for streaming drone data to connected clients.
    Runs send and receive concurrently for smooth, unblocked telemetry streaming.
    """
    await websocket.accept()
    connected_websockets.add(websocket)
    print(f"WebSocket client connected. Total clients: {len(connected_websockets)}")

    try:
        # Send initial connection message
        await websocket.send_json({
            "type": "connection",
            "status": "connected",
            "message": "Connected to drone data stream"
        })

        # Run receive and broadcast tasks concurrently
        broadcast_task = asyncio.create_task(_broadcast_telemetry(websocket))
        receive_task = asyncio.create_task(_receive_commands(websocket))
        
        # Wait for either task to fail (connection lost or error)
        done, pending = await asyncio.wait(
            [broadcast_task, receive_task],
            return_when=asyncio.FIRST_EXCEPTION
        )
        
        # Cancel the remaining task
        for task in pending:
            task.cancel()
            try:
                await task
            except asyncio.CancelledError:
                pass

    except WebSocketDisconnect:
        print(f"WebSocket client disconnected. Total clients: {len(connected_websockets) - 1}")
    except Exception as e:
        print(f"WebSocket error: {e}")
    finally:
        # Ensure tasks are cleaned up
        try:
            broadcast_task.cancel()
            receive_task.cancel()
        except (NameError, asyncio.CancelledError):
            pass
        connected_websockets.discard(websocket)


@app.get("/status")
async def status():
    """Returns the current system status."""
    async with vehicle_data_lock:
        return {
            "connected_websockets": len(connected_websockets),
            "vehicle_connected": vehicle is not None,
            "latest_vehicle_data": latest_vehicle_data if latest_vehicle_data else None
        }

@app.post("/agent/command")
async def agent_command(request: Request):
    global agent_runner

    body_bytes = await request.body()
    text = body_bytes.decode("utf-8").strip() if body_bytes else ""

    if not text:
        return JSONResponse(status_code=400, content={"error": "empty prompt"})

    if agent_runner is None:
        return JSONResponse(status_code=503, content={"error": "agent not ready"})

    try:
        fn = getattr(agent_runner, "run", agent_runner)
        result = await asyncio.to_thread(fn, text)

        # Extract agent content the same way we do for websockets
        def _extract_agent_content(res):
            if res is None:
                return None
            if isinstance(res, (str, int, float, bool)):
                return res
            if isinstance(res, dict):
                for k in ("content", "data", "text", "result", "output", "commands", "results"):
                    if k in res:
                        return res[k]
                return res
            for attr in ("content", "text", "result", "output", "data"):
                if hasattr(res, attr):
                    try:
                        return getattr(res, attr)
                    except Exception:
                        continue
            try:
                d = getattr(res, "__dict__", None)
                if isinstance(d, dict):
                    for k in ("content", "data", "text", "result", "output", "commands", "results"):
                        if k in d:
                            return d[k]
            except Exception:
                pass
            return str(res)

        payload = _extract_agent_content(result)
        try:
            return JSONResponse(content=payload)
        except Exception:
            return JSONResponse(content={"result": str(payload)})
    except Exception as e:
        return JSONResponse(status_code=500, content={"error": str(e)})

# Mount static files LAST so WebSocket and API routes take precedence
if BUILD_PATH.exists():
    app.mount("/", StaticFiles(directory=str(BUILD_PATH), html=True), name="static")
    print(f"✓ Static files mounted at / from: {BUILD_PATH}")
else:
    print("Couldn't find WebGL build!")
    exit()


if __name__ == "__main__":
    print("Starting AVA Drone WebSocket Server...")
    
    uvicorn.run(
        app,
        host="127.0.0.1",
        port=8000,
        log_level="info"
    )

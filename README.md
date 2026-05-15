# Mission Planner Web Viewer

## Setup & Running

```bash
# (Important: in Control folder!)
cd Control
python -m venv .
pip install -r requirements.txt

# For the web build, use server.py
# This will serve the build and communicate with Mission Planner
python server.py
```

## Mapbox Access Token

Set your Mapbox token in `Assets/Resources/Mapbox/MapboxConfiguration.txt` by replacing
`YOUR_MAPBOX_ACCESS_TOKEN` with your own access token. Do not commit real tokens.


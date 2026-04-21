let ws;
let wsConnected = false;

const statusEl = document.getElementById("ws-status");
const outputEl = document.getElementById("cmd-output");
const inputEl = document.getElementById("cmd-input");

const wsProtocol = location.protocol === "https:" ? "wss" : "ws";
const wsUrl = `${wsProtocol}://${location.host}/ws`;

function setStatus(text) {
  if (statusEl) statusEl.textContent = text;
}

function connectWS() {
  try {
    ws = new WebSocket(wsUrl);
  } catch {
    setStatus("Error");
    return;
  }

  ws.addEventListener("open", () => {
    wsConnected = true;
    setStatus("Connected");
  });

  ws.addEventListener("close", () => {
    wsConnected = false;
    setStatus("Disconnected");
    setTimeout(connectWS, 2500);
  });

  ws.addEventListener("error", () => {
    setStatus("Error");
  });

  ws.addEventListener("message", (evt) => {
    try {
      const j = JSON.parse(evt.data);

      if (j?.type === "agent_result") {
        const data = typeof j.data === "string"
          ? j.data
          : JSON.stringify(j.data, null, 2);

        outputEl.textContent = data;
      }

      if (j?.type === "connection") {
        setStatus("Connected");
      }
    } catch {
      setStatus("Bad Message");
    }
  });
}

function sendCommand(text) {
  if (wsConnected && ws?.readyState === WebSocket.OPEN) {
    ws.send(text);
    outputEl.textContent = "Sent via WebSocket...";
    return;
  }

  fetch("/agent/command", {
    method: "POST",
    headers: { "Content-Type": "text/plain" },
    body: text,
  })
    .then((r) => r.text())
    .then((t) => (outputEl.textContent = t))
    .catch((e) => (outputEl.textContent = String(e)));
}

document.getElementById("send-cmd").addEventListener("click", () => {
  const value = inputEl.value?.trim();

  if (!value) {
    outputEl.textContent = "Empty input";
    return;
  }

  sendCommand(value);
});

connectWS();

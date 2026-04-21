const canvas = document.querySelector("#unity-canvas");

function unityShowBanner(msg, type) {
  const warningBanner = document.querySelector("#unity-warning");

  function update() {
    warningBanner.style.display = warningBanner.children.length ? "block" : "none";
  }

  const div = document.createElement("div");
  div.innerHTML = msg;
  warningBanner.appendChild(div);

  if (type === "error") {
    div.style.background = "red";
    div.style.padding = "10px";
  } else if (type === "warning") {
    div.style.background = "yellow";
    div.style.padding = "10px";

    setTimeout(() => {
      warningBanner.removeChild(div);
      update();
    }, 5000);
  }

  update();
}

const buildUrl = "Build";

const config = {
  dataUrl: buildUrl + "/Build.data",
  frameworkUrl: buildUrl + "/Build.framework.js",
  codeUrl: buildUrl + "/Build.wasm",
  streamingAssetsUrl: "StreamingAssets",
  companyName: "DefaultCompany",
  productName: "dronekittest",
  productVersion: "0.1.0",
  showBanner: unityShowBanner,
};
config.matchWebGLToCanvasSize = false;
config.devicePixelRatio = 1;

function initUnity() {
  const script = document.createElement("script");
  script.src = buildUrl + "/Build.loader.js";

  script.onload = () => {
    createUnityInstance(canvas, config, (progress) => {
      document.querySelector("#unity-progress-bar-full").style.width =
        100 * progress + "%";
    })
      .then((unityInstance) => {
        document.querySelector("#unity-loading-bar").style.display = "none";

        document.querySelector("#unity-fullscreen-button").onclick = () => {
          unityInstance.SetFullscreen(1);
        };
      })
      .catch(alert);
  };

  document.body.appendChild(script);
}

initUnity();

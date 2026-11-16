// JavaScript-driven animation: bars and clock, plus bridge functions callable from TiXL.
const bars = document.getElementById("bars");
const clock = document.getElementById("clock");
const card = document.getElementById("card");
const title = document.getElementById("title");

const barElements = [];
for (let i = 0; i < 16; i++) {
  const bar = document.createElement("div");
  bar.className = "bar";
  bars.appendChild(bar);
  barElements.push(bar);
}

function frame(timeMs) {
  const t = timeMs / 1000;
  barElements.forEach((bar, i) => {
    const level = 0.5 + 0.5 * Math.sin(t * 2.2 + i * 0.5);
    bar.style.transform = "scaleY(" + (0.15 + 0.85 * level).toFixed(3) + ")";
  });
  clock.textContent = "t = " + t.toFixed(1) + " s";
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);

// Functions callable from TiXL CallJavaScript operator:
window.setHue = (value) => card.style.setProperty("--hue", String(value));
window.setTitle = (text) => { title.textContent = String(text); };
console.log("hello page ready");

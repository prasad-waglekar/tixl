// JavaScript-driven animation: bars, a clock and a slowly cycling CSS variable.
const bars = document.getElementById("bars");
const clock = document.getElementById("clock");
const card = document.getElementById("card");

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
  card.style.setProperty("--hue", String(Math.round((200 + t * 10) % 360)));
  clock.textContent = "t = " + t.toFixed(1) + " s";
  requestAnimationFrame(frame);
}
requestAnimationFrame(frame);

// Functions for later phases (called from TiXL through the JavaScript bridge).
window.setHue = (value) => card.style.setProperty("--hue", String(value));
console.log("hello page ready");

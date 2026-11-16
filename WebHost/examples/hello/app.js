// JavaScript-driven animation: driven directly by TiXL timeline time via window.setTiXLTime(t).
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

// Driven directly by TiXL's timeline playback head
window.setTiXLTime = function(t) {
  barElements.forEach((bar, i) => {
    const level = 0.5 + 0.5 * Math.sin(t * 2.2 + i * 0.5);
    bar.style.transform = "scaleY(" + (0.15 + 0.85 * level).toFixed(3) + ")";
  });
  card.style.setProperty("--hue", String(Math.round((200 + t * 10) % 360)));
  clock.textContent = "t = " + t.toFixed(2) + " s";
};

console.log("hello page ready");

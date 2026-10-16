(() => {
  const key = "share.me.theme";
  let theme = "dark";
  try {
    if (localStorage.getItem(key) === "light") theme = "light";
  } catch {
    // Theme switching also works when storage is unavailable.
  }
  function apply() {
    document.documentElement.dataset.theme = theme;
    document.querySelector('meta[name="theme-color"]').content =
      theme === "dark" ? "#111714" : "#f5f5ef";
    document.querySelectorAll(".theme-toggle").forEach((button) => {
      const next = theme === "dark" ? "light" : "dark";
      button.textContent = `${next === "light" ? "Light" : "Dark"} theme`;
      button.setAttribute("aria-label", `Switch to ${next} theme`);
    });
  }
  // This script runs before CSS so a saved light preference never flashes dark.
  apply();
  document.addEventListener("DOMContentLoaded", () => {
    apply();
    document.querySelectorAll(".theme-toggle").forEach((button) => {
      button.addEventListener("click", () => {
        theme = theme === "dark" ? "light" : "dark";
        apply();
        try {
          localStorage.setItem(key, theme);
        } catch {}
      });
    });
  });
})();

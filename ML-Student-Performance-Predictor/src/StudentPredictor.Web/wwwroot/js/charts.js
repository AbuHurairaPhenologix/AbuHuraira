// Shared Chart.js defaults for the dark theme.
const CATEGORY_COLORS = { 'Excellent': '#199e70', 'Good': '#3987e5', 'Average': '#bf8410', 'At Risk': '#d03b3b' };

Chart.defaults.color = '#c3c2b7';
Chart.defaults.borderColor = 'rgba(255,255,255,0.06)';
Chart.defaults.font.family = "system-ui, -apple-system, 'Segoe UI', sans-serif";
Chart.defaults.font.size = 12;
Chart.defaults.plugins.legend.labels.boxWidth = 10;
Chart.defaults.plugins.legend.labels.boxHeight = 10;
Chart.defaults.plugins.tooltip.backgroundColor = '#242422';
Chart.defaults.plugins.tooltip.borderColor = '#383835';
Chart.defaults.plugins.tooltip.borderWidth = 1;
Chart.defaults.plugins.tooltip.padding = 10;
Chart.defaults.maintainAspectRatio = false;
Chart.defaults.animation = false;

function axisTitle(text) {
  return { display: true, text, color: '#898781' };
}

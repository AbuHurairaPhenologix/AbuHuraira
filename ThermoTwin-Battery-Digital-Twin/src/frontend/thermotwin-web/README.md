# ThermoTwin.NET — Angular dashboard

Angular 21 (standalone components, signals) front end for the ThermoTwin battery thermal digital twin.

```bash
npm install
npm start            # http://localhost:4200, proxies /api and /hubs to http://localhost:5180
npm run build        # production build to dist/
npx ng test --watch=false
```

Structure:

- `src/app/core` — API client, SignalR-backed `TwinStore`, `ExperimentStore`, DTO models, formatters
- `src/app/shared` — canvas `HeatmapComponent`, Chart.js `ChartComponent` + theme, KPI/risk/card components, colormaps
- `src/app/pages` — Overview, Simulation Setup, Live Digital Twin, Thermal Analysis, Inverse Problem, Cooling Optimisation, Numerical Validation, Experiment Results

See the repository [README](../../../README.md) for the full project description.

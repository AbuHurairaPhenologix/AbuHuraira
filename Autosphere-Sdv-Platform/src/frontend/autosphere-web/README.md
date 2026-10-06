# autosphere-web

Angular dashboard ("digital twin") of the AutoSphere platform. Standalone components, signals and
zoneless change detection; live data via the SignalR hub `/hubs/vehicles`, initial loads via REST.

```bash
npm ci
npm start            # http://localhost:4200, proxies /api, /hubs and /health to http://localhost:5080
npm run build        # production build into dist/autosphere-web
npm test -- --watch=false
```

See the repository README for running the backend and the vehicle gateway.

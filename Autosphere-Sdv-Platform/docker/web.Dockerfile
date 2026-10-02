# syntax=docker/dockerfile:1
# AutoSphere dashboard: Angular production build served by nginx, which also reverse-proxies the API.

FROM node:24-alpine AS build
WORKDIR /app
COPY src/frontend/autosphere-web/package.json src/frontend/autosphere-web/package-lock.json ./
RUN npm ci --no-audit --no-fund
COPY src/frontend/autosphere-web/ ./
RUN npx ng build --configuration production

FROM nginx:1.29-alpine AS runtime
COPY docker/nginx/default.conf /etc/nginx/conf.d/default.conf
COPY --from=build /app/dist/autosphere-web/browser /usr/share/nginx/html
EXPOSE 80

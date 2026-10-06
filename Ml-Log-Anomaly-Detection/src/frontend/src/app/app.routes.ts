import { Routes } from '@angular/router';
import { authGuard } from './core/auth.interceptor';

export const routes: Routes = [
  { path: 'login', loadComponent: () => import('./pages/login/login.component').then((m) => m.LoginComponent) },
  {
    path: '',
    canActivate: [authGuard],
    children: [
      { path: '', pathMatch: 'full', redirectTo: 'overview' },
      { path: 'overview', loadComponent: () => import('./pages/overview/overview.component').then((m) => m.OverviewComponent) },
      { path: 'anomalies', loadComponent: () => import('./pages/anomalies/anomalies.component').then((m) => m.AnomaliesComponent) },
      { path: 'anomalies/:id', loadComponent: () => import('./pages/anomaly-detail/anomaly-detail.component').then((m) => m.AnomalyDetailComponent) },
      { path: 'events', loadComponent: () => import('./pages/events/events.component').then((m) => m.EventsComponent) },
      { path: 'models', loadComponent: () => import('./pages/models/models.component').then((m) => m.ModelsComponent) },
      { path: 'health', loadComponent: () => import('./pages/health/health.component').then((m) => m.HealthComponent) },
    ],
  },
  { path: '**', redirectTo: '' },
];

import { Routes } from '@angular/router';
import { adminGuard, authGuard } from './core/auth.service';
import { Shell } from './layout/shell';
import { LoginPage } from './pages/login';

export const routes: Routes = [
  { path: 'login', component: LoginPage, title: 'Sign in · AutoSphere' },
  {
    path: '',
    component: Shell,
    canActivate: [authGuard],
    children: [
      { path: 'dashboard', loadComponent: () => import('./pages/dashboard').then((m) => m.DashboardPage), title: 'Dashboard · AutoSphere' },
      { path: 'ecus', loadComponent: () => import('./pages/ecus').then((m) => m.EcusPage), title: 'ECUs · AutoSphere' },
      { path: 'telemetry', loadComponent: () => import('./pages/telemetry').then((m) => m.TelemetryPage), title: 'Telemetry · AutoSphere' },
      { path: 'diagnostics', loadComponent: () => import('./pages/diagnostics').then((m) => m.DiagnosticsPage), title: 'Diagnostics · AutoSphere' },
      { path: 'ota', loadComponent: () => import('./pages/ota').then((m) => m.OtaPage), title: 'OTA updates · AutoSphere' },
      { path: 'alerts', loadComponent: () => import('./pages/alerts').then((m) => m.AlertsPage), title: 'Alerts · AutoSphere' },
      { path: 'faults', canActivate: [adminGuard], loadComponent: () => import('./pages/faults').then((m) => m.FaultsPage), title: 'Fault injection · AutoSphere' },
      { path: 'users', canActivate: [adminGuard], loadComponent: () => import('./pages/users').then((m) => m.UsersPage), title: 'Users · AutoSphere' },
      { path: '', pathMatch: 'full', redirectTo: 'dashboard' },
    ],
  },
  { path: '**', redirectTo: '' },
];

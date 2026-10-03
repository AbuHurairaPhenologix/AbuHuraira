import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: 'overview', title: 'Overview · ThermoTwin.NET', loadComponent: () => import('./pages/overview.page').then((m) => m.OverviewPage) },
  { path: 'configure', title: 'Simulation Setup · ThermoTwin.NET', loadComponent: () => import('./pages/configure.page').then((m) => m.ConfigurePage) },
  { path: 'twin', title: 'Live Digital Twin · ThermoTwin.NET', loadComponent: () => import('./pages/live-twin.page').then((m) => m.LiveTwinPage) },
  { path: 'thermal', title: 'Thermal Analysis · ThermoTwin.NET', loadComponent: () => import('./pages/thermal.page').then((m) => m.ThermalPage) },
  { path: 'inverse', title: 'Inverse Problem · ThermoTwin.NET', loadComponent: () => import('./pages/inverse.page').then((m) => m.InversePage) },
  { path: 'cooling', title: 'Cooling Optimisation · ThermoTwin.NET', loadComponent: () => import('./pages/cooling.page').then((m) => m.CoolingPage) },
  { path: 'validation', title: 'Numerical Validation · ThermoTwin.NET', loadComponent: () => import('./pages/validation.page').then((m) => m.ValidationPage) },
  { path: 'experiments', title: 'Experiment Results · ThermoTwin.NET', loadComponent: () => import('./pages/experiments.page').then((m) => m.ExperimentsPage) },
  { path: '**', redirectTo: 'overview' },
];

import { Routes } from '@angular/router';

export const routes: Routes = [
  { path: '', pathMatch: 'full', redirectTo: 'overview' },
  { path: 'overview', title: 'Overview · ThermoTwin.NET', loadComponent: () => import('./pages/overview.page').then((m) => m.OverviewPage) },
  { path: 'configure', title: 'Simulation Setup · ThermoTwin.NET', loadComponent: () => import('./pages/configure.page').then((m) => m.ConfigurePage) },
  { path: 'twin', title: 'Live Digital Twin · ThermoTwin.NET', loadComponent: () => import('./pages/live-twin.page').then((m) => m.LiveTwinPage) },
  { path: 'thermal', title: 'Thermal Analysis · ThermoTwin.NET', loadComponent: () => import('./pages/thermal.page').then((m) => m.ThermalPage) },
  { path: 'inverse', title: 'Inverse Problem · ThermoTwin.NET', loadComponent: () => import('./pages/inverse.page').then((m) => m.InversePage) },
  { path: 'cooling', title: 'Cooling Optimisation · ThermoTwin.NET', loadComponent: () => import('./pages/cooling.page').then((m) => m.CoolingPage) },
  { path: 'model', title: 'Mathematical Model · ThermoTwin.NET', loadComponent: () => import('./pages/model.page').then((m) => m.ModelPage) },
  { path: 'methods', title: 'Numerical Methods · ThermoTwin.NET', loadComponent: () => import('./pages/methods.page').then((m) => m.MethodsPage) },
  { path: 'rom', title: 'Reduced-Order Model · ThermoTwin.NET', loadComponent: () => import('./pages/rom.page').then((m) => m.RomPage) },
  { path: 'optimization', title: 'PDE-Constrained Optimisation · ThermoTwin.NET', loadComponent: () => import('./pages/optimization.page').then((m) => m.OptimizationPage) },
  { path: 'identifiability', title: 'Identifiability · ThermoTwin.NET', loadComponent: () => import('./pages/identifiability.page').then((m) => m.IdentifiabilityPage) },
  { path: 'validation', title: 'Numerical Validation · ThermoTwin.NET', loadComponent: () => import('./pages/validation.page').then((m) => m.ValidationPage) },
  { path: 'experiments', title: 'Experiment Results · ThermoTwin.NET', loadComponent: () => import('./pages/experiments.page').then((m) => m.ExperimentsPage) },
  { path: '**', redirectTo: 'overview' },
];

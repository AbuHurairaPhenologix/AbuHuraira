import { ChangeDetectionStrategy, Component, OnInit, computed, inject } from '@angular/core';
import { RouterLink, RouterLinkActive, RouterOutlet } from '@angular/router';
import { ApiService } from './core/api.service';
import { clock } from './core/format';
import { CoolingMode } from './core/models';
import { TwinStore } from './core/twin-store.service';
import { RiskBadgeComponent } from './shared/ui';

interface NavItem {
  path: string;
  label: string;
  icon: string;
}

@Component({
  selector: 'app-root',
  imports: [RouterOutlet, RouterLink, RouterLinkActive, RiskBadgeComponent],
  changeDetection: ChangeDetectionStrategy.OnPush,
  templateUrl: './app.html',
  styleUrl: './app.scss',
})
export class App implements OnInit {
  private readonly store = inject(TwinStore);
  private readonly api = inject(ApiService);

  readonly frame = this.store.frame;
  readonly connection = this.store.connection;
  readonly progress = this.store.progress;
  readonly clock = clock;

  readonly sections: { title: string; items: NavItem[] }[] = [
    {
      title: 'Digital twin',
      items: [
        { path: '/overview', label: 'Overview', icon: '◧' },
        { path: '/twin', label: 'Live Digital Twin', icon: '◉' },
        { path: '/configure', label: 'Simulation Setup', icon: '⚙' },
      ],
    },
    {
      title: 'Mathematics',
      items: [
        { path: '/model', label: 'Mathematical Model', icon: '∂' },
        { path: '/methods', label: 'FVM · FEM', icon: '△' },
        { path: '/inverse', label: 'Inverse Problem', icon: '⟲' },
        { path: '/identifiability', label: 'Identifiability', icon: '≈' },
        { path: '/rom', label: 'Reduced-Order Model', icon: 'Φ' },
        { path: '/optimization', label: 'PDE-Constrained Opt.', icon: '∇' },
      ],
    },
    {
      title: 'Analysis',
      items: [
        { path: '/thermal', label: 'Thermal Analysis', icon: '∿' },
        { path: '/cooling', label: 'Cooling Optimisation', icon: '❄' },
      ],
    },
    {
      title: 'Research',
      items: [
        { path: '/validation', label: 'Numerical Validation', icon: '∑' },
        { path: '/experiments', label: 'Experiment Results', icon: '▤' },
      ],
    },
  ];

  readonly running = computed(() => this.frame()?.status === 'Running');
  readonly paused = computed(() => this.frame()?.status === 'Paused');

  ngOnInit(): void {
    this.store.start();
  }

  togglePause(): void {
    (this.running() ? this.api.pause() : this.api.resume()).subscribe();
  }

  setMode(mode: string): void {
    this.api.setMode(mode as CoolingMode).subscribe();
  }
}

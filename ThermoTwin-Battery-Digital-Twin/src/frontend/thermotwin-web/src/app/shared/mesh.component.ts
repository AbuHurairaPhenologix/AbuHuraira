import { ChangeDetectionStrategy, Component, computed, input } from '@angular/core';
import { meshEdges } from '../core/math-format';
import { FemMeshDto } from '../core/math-models';

/** SVG drawing of a P1 triangulation: element edges, boundary edges highlighted, nodes as dots. */
@Component({
  selector: 'tt-mesh',
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    @if (mesh(); as m) {
      <svg [attr.viewBox]="'-6 -6 ' + (width + 12) + ' ' + (height() + 12)" role="img" [attr.aria-label]="'Finite-element mesh with ' + m.elements + ' triangles'">
        @for (t of shaded(); track $index) {
          <polygon [attr.points]="t" class="tri" />
        }
        @for (e of interior(); track $index) {
          <line [attr.x1]="e[0]" [attr.y1]="e[1]" [attr.x2]="e[2]" [attr.y2]="e[3]" class="edge" />
        }
        <rect x="0" y="0" [attr.width]="width" [attr.height]="height()" class="boundary" />
        @for (p of points(); track $index) {
          <circle [attr.cx]="p[0]" [attr.cy]="p[1]" r="2.2" class="node" />
        }
      </svg>
      <div class="cap">
        {{ m.nx }}×{{ m.ny }} rectangles → <b>{{ m.elements }}</b> P1 triangles, <b>{{ m.nodes }}</b> nodes,
        {{ m.boundarySegments }} boundary edges (Robin)
      </div>
    }
  `,
  styles: `
    :host { display: block; }
    svg { width: 100%; display: block; }
    .tri { fill: rgba(57, 135, 229, 0.05); }
    .tri:nth-child(4n) { fill: rgba(57, 135, 229, 0.11); }
    .edge { stroke: #5b6b80; stroke-width: 0.9; }
    .boundary { fill: none; stroke: #d95926; stroke-width: 2.2; }
    .node { fill: #c3c2b7; }
    .cap { font-size: 11px; color: var(--ink-3); margin-top: 6px; }
    .cap b { color: var(--ink-1); }
  `,
})
export class MeshComponent {
  readonly mesh = input<FemMeshDto | null | undefined>(null);
  readonly width = 560;

  private readonly scale = computed(() => this.width / (this.mesh()?.lengthX ?? 0.2));
  readonly height = computed(() => (this.mesh()?.lengthY ?? 0.1) * this.scale());

  private xy(i: number): [number, number] {
    const m = this.mesh()!;
    const [x, y] = m.nodeXy[i];
    return [x * this.scale(), this.height() - y * this.scale()];
  }

  readonly points = computed(() => (this.mesh()?.nodeXy ?? []).map((_, i) => this.xy(i)));

  readonly interior = computed(() => meshEdges(this.mesh()?.triangles ?? []).map(([a, b]) => [...this.xy(a), ...this.xy(b)]));

  readonly shaded = computed(() =>
    (this.mesh()?.triangles ?? []).map((t) =>
      t
        .map((i) => this.xy(i))
        .map(([x, y]) => `${x.toFixed(1)},${y.toFixed(1)}`)
        .join(' '),
    ),
  );
}

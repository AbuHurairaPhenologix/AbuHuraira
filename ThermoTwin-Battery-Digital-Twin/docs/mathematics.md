# Mathematics of ThermoTwin.NET

This document derives every algorithm implemented in `src/backend/ThermoTwin.Numerics`. Each section names the file that implements it.

---

## 1. Physical model

### 1.1 Governing equation

A large-format pouch cell is thin (δ = 10 mm) compared with its in-plane size (200 × 100 mm), so the temperature is averaged through the thickness. Integrating the 3-D heat equation over the thickness, with a cold plate on one face (heat-transfer coefficient h(u)), gives

$$
\rho c_p \frac{\partial T}{\partial t} = k\,\nabla^2 T + q(x,y,t) - H(u)\,(T - T_c), \qquad H(u) = \frac{h(u)}{\delta}.
$$

| Symbol | Unit | Meaning |
|---|---|---|
| T | °C | depth-averaged temperature |
| ρ, c<sub>p</sub> | kg/m³, J/(kg·K) | density, specific heat |
| k | W/(m·K) | effective in-plane conductivity of the electrode stack |
| q | W/m³ | volumetric heat generation (Joule heating + defect) |
| h(u) = h<sub>min</sub> + u(h<sub>max</sub> − h<sub>min</sub>) | W/(m²·K) | cold-plate coefficient, u ∈ [0, 1] the pump level |
| T<sub>c</sub> | °C | coolant temperature |

Dividing by ρc<sub>p</sub> gives the diffusivity α = k/(ρc<sub>p</sub>) = 8·10⁻⁶ m²/s for the demo cell, and a diffusion time L<sub>y</sub>²/α ≈ 1250 s, comparable to the 30-minute charge.

*Implementation:* `Physics/ThermalModel.cs`.

### 1.2 Boundary conditions

On each edge (outward normal n):

| Kind | Condition |
|---|---|
| Dirichlet | T = T<sub>b</sub> |
| Neumann | −k ∂T/∂n = g (g = 0 is an insulated edge) |
| Robin | −k ∂T/∂n = h<sub>e</sub>(T − T<sub>∞</sub>) (convection to ambient) |

### 1.3 Heat sources and load

Ohmic heating is resistive, so both the bulk Joule heat and the extra heat of a high-resistance defect scale with the square of the current:

$$
q(x,y,t) = s(t)\,\Bigl[q_J + \sum_m Q_m\, e^{-\lVert \mathbf x-\mathbf x_m\rVert^2/2\sigma_m^2}\Bigr], \qquad s(t) = \bigl(I(t)/I_{ref}\bigr)^2 .
$$

The battery management system measures I(t), so **s(t) is known**. The spatial shape in brackets is unknown. The demo uses a CC–CV fast-charge profile (`Physics/HeatSource.cs`).

---

## 2. Spatial discretisation (`Pde/DiscreteLaplacian.cs`)

The grid is cell-centred: cell (i, j) has its centre at ((i + ½)Δx, (j + ½)Δy). The finite-volume flux balance gives the five-point Laplacian

$$
(\nabla^2 T)_{ij} \approx \frac{T_{i+1,j} - 2T_{ij} + T_{i-1,j}}{\Delta x^2} + \frac{T_{i,j+1} - 2T_{ij} + T_{i,j-1}}{\Delta y^2}.
$$

### Ghost cells

A boundary face lies h/2 from the adjacent cell centre P. A ghost value T<sub>g</sub> is defined so that the stencil above still applies:

- **Dirichlet:** the face value (T<sub>P</sub> + T<sub>g</sub>)/2 = T<sub>b</sub> ⇒ T<sub>g</sub> = 2T<sub>b</sub> − T<sub>P</sub>. Contribution: −2T<sub>P</sub>/h² + 2T<sub>b</sub>/h².
- **Neumann:** −k(T<sub>g</sub> − T<sub>P</sub>)/h = g ⇒ contribution −g/(k h).
- **Robin:** −k(T<sub>g</sub> − T<sub>P</sub>)/h = h<sub>e</sub>((T<sub>P</sub> + T<sub>g</sub>)/2 − T<sub>∞</sub>). With β = h<sub>e</sub>h/k, solving for T<sub>g</sub> gives

$$
\frac{T_g - T_P}{h^2} = \frac{\gamma\,(T_\infty - T_P)}{h^2}, \qquad \gamma = \frac{\beta}{1 + \beta/2}.
$$

  As h<sub>e</sub> → ∞, γ → 2 (Dirichlet). As h<sub>e</sub> → 0, γ → 0 (insulated).

The result is the linear operator ∇²T ≈ A T + b, where A is **symmetric** and negative (semi-)definite, and b collects the boundary data.

### Ordering and bandwidth

Unknowns are numbered k = i·N<sub>y</sub> + j, so the short side varies fastest. Neighbours in y are ±1 apart and neighbours in x are ±N<sub>y</sub> apart, so the **half-bandwidth is p = N<sub>y</sub>** (20 for the 40×20 grid) instead of N<sub>x</sub>.

### Semi-discrete system

$$
\frac{d\mathbf T}{dt} = M(u)\,\mathbf T + \mathbf f(t), \qquad
M(u) = \alpha A - \frac{H(u)}{\rho c_p}\,I, \qquad
\mathbf f = \alpha\,\mathbf b + \frac{\mathbf q}{\rho c_p} + \frac{H(u)}{\rho c_p}\,T_c .
$$

---

## 3. Time integration (`Pde/HeatEquationSolver.cs`)

### θ-method

$$
(I - \theta\Delta t\,M)\,\mathbf T^{n+1} = (I + (1-\theta)\Delta t\,M)\,\mathbf T^{n} + \Delta t\,[\theta\,\mathbf f^{n+1} + (1-\theta)\,\mathbf f^{n}]
$$

| θ | Scheme | Order in time | Stability |
|---|---|---|---|
| 0 | explicit Euler | 1 | Δt ≤ 2/ρ(M) |
| 1 | implicit Euler | 1 | unconditional, L-stable |
| ½ | Crank–Nicolson | 2 | unconditional (A-stable) |

Cooling u is held constant over a step, so M(u) is fixed within it.

### Banded Cholesky (`LinearAlgebra/BandedCholesky.cs`)

S = I − θΔt M is symmetric positive definite. Its lower band (p + 1 diagonals) is factorised as S = L Lᵀ:

$$
l_{jj} = \sqrt{s_{jj} - \textstyle\sum_{k=j-p}^{j-1} l_{jk}^2}, \qquad
l_{ij} = \Bigl(s_{ij} - \textstyle\sum_{k=i-p}^{j-1} l_{ik}l_{jk}\Bigr)/l_{jj}, \quad j < i \le j+p.
$$

Factorisation costs O(n p²) and each forward/backward solve costs O(n p). For n = 800 and p = 20 a step is about 3·10⁴ flops (0.06 ms measured). Factors are cached per cooling level in a thread-safe dictionary, so the many forward solves of the inverse problem and the optimiser share them. A conjugate-gradient solver (`ConjugateGradient.cs`) independently cross-checks the implicit step in the test suite.

### Stability analysis (`Pde/StabilityAnalyzer.cs`)

M is symmetric negative definite with spectrum in [−ρ(M), 0). The θ-scheme amplifies an eigenmode λ by

$$
G(z) = \frac{1 + (1-\theta) z}{1 - \theta z}, \qquad z = \Delta t\,\lambda .
$$

For explicit Euler, |G| ≤ 1 requires Δt ≤ 2/ρ(M). ρ(M) is estimated by **power iteration** from a checkerboard start vector and bounded above by **Gershgorin's theorem**, ρ(M) ≤ max<sub>i</sub> Σ<sub>j</sub>|m<sub>ij</sub>|. For the textbook interior stencil this reproduces Δt ≤ 1/(2α(Δx⁻² + Δy⁻²)). For Crank–Nicolson, G(−∞) = −1: stiff modes are damped slowly and with alternating sign. The stability report exposes this "stiff-mode amplification".

### Energy conservation

With insulated edges and no cooling, summing the finite-volume equations over all cells cancels the interior fluxes exactly:

$$
\sum_{ij} \rho c_p\,(T^{n+1}_{ij} - T^n_{ij})\,V = \Delta t\,\sum_{ij} \tfrac{1}{2}(q^n_{ij} + q^{n+1}_{ij})\,V .
$$

This holds for every θ. The validation reproduces it to 10⁻¹⁴ relative error.

---

## 4. Verification problem (`Validation/ConvergenceStudy.cs`)

Take T = T<sub>b</sub> on all edges and q = 0. Then

$$
T(x,y,t) = T_b + A\,e^{-\alpha\pi^2(L_x^{-2} + L_y^{-2})t}\,\sin\frac{\pi x}{L_x}\sin\frac{\pi y}{L_y}.
$$

The sine mode sampled at cell centres satisfies the Dirichlet ghost-cell relation exactly: the ghost at x = −Δx/2 equals −sin(πΔx/2L<sub>x</sub>) = −T<sub>P</sub>. It is therefore an **exact eigenvector** of the discrete operator, with eigenvalue

$$
\mu_h = \alpha\Bigl[\frac{2\cos(\pi\Delta x/L_x) - 2}{\Delta x^2} + \frac{2\cos(\pi\Delta y/L_y) - 2}{\Delta y^2}\Bigr] = -\alpha\pi^2(L_x^{-2}+L_y^{-2}) + O(h^2).
$$

This gives two independent studies:

- **Spatial:** compare with the PDE solution while refining h with Δt ∝ h², so time error is O(h²) for every scheme. Expected order 2.
- **Temporal:** compare with the exact *semi-discrete* solution e<sup>μ<sub>h</sub>t</sup>v. Only time error remains. Expected order 1 for the Euler schemes and 2 for Crank–Nicolson.

The observed order is p = log(e<sub>coarse</sub>/e<sub>fine</sub>) / log(h<sub>coarse</sub>/h<sub>fine</sub>).

---

## 5. Sensors and noise (`Sensors/SensorNetwork.cs`)

Each thermistor reads the bilinear interpolation of the four surrounding cell centres, so y = C T with C a sparse 12 × 800 matrix (four weights per row). This reading is exact for linear fields, which a unit test checks. Measurement noise is ε ~ N(0, σ²I), generated by a seeded Box–Muller transform so that every experiment is reproducible.

---

## 6. The inverse heat-source problem (`Inverse/`)

### 6.1 Parameterisation

The unknown spatial shape is expanded in bilinear "hat" functions on a coarse m<sub>x</sub> × m<sub>y</sub> node lattice (13 × 7 = 91 in the demo, 16.7 mm spacing):

$$
q(x,y,t) = s(t)\sum_{j=1}^{n} q_j\,\varphi_j(x,y).
$$

Reducing 800 grid values to 91 smooth coefficients is the first, implicit, regularisation.

### 6.2 Linear superposition

The semi-discrete system is linear in (T, q). Therefore

$$
\mathbf T(t) = \mathbf T_0(t) + \sum_j q_j\,\mathbf T_j(t),
$$

where

- **T₀** solves the full model with q = 0: the known initial state, boundary data, coolant and cooling history u(t);
- **T<sub>j</sub>** solves the *homogeneous* model (zero initial state, zero boundary and coolant data) driven by the source s(t)φ<sub>j</sub>, under the same cooling history.

`HeatEquationSolver.Step(..., homogeneous: true)` implements the homogeneous variant.

### 6.3 Measurement equation

Stacking all sensors s at all sample times t:

$$
\underbrace{\mathbf y - C\,\mathbf T_0}_{\mathbf d} = A\,\mathbf q + \boldsymbol\varepsilon, \qquad A_{(t,s),j} = (C\,\mathbf T_j(t))_s .
$$

After the 30-minute charge, A has m = 12 × 360 = 4320 rows and n = 91 columns. Diffusion is a smoothing operator: a high-frequency component of q reaches the sensors attenuated roughly like e<sup>−α|k|²t</sup>. So A has rapidly decaying singular values, and the naive least-squares solution (AᵀA)⁻¹Aᵀd amplifies noise without bound. This is a discrete **ill-posed problem** in the sense of Hadamard (instability).

### 6.4 Recursive assembly

The estimator never stores A. At each step it advances T₀ and the 91 responses T<sub>j</sub> (in parallel) and, for the new block row a<sub>s</sub>, accumulates

$$
A^\top A \mathrel{+}= \mathbf a_s \mathbf a_s^\top, \qquad A^\top\mathbf d \mathrel{+}= d_s\,\mathbf a_s, \qquad \mathbf d^\top\mathbf d \mathrel{+}= d_s^2 .
$$

Memory is O(n²) and the cost per step is independent of history length. The residual of any candidate q follows without A:

$$
\lVert A\mathbf q - \mathbf d\rVert^2 = \mathbf q^\top A^\top A\,\mathbf q - 2\,\mathbf q^\top A^\top\mathbf d + \mathbf d^\top\mathbf d .
$$

### 6.5 Tikhonov regularisation

$$
\mathbf q_\lambda = \arg\min_{\mathbf q}\ \lVert A\mathbf q - \mathbf d\rVert^2 + \lambda\lVert L\mathbf q\rVert^2
\quad\Longleftrightarrow\quad
(A^\top A + \lambda L^\top L)\,\mathbf q_\lambda = A^\top\mathbf d .
$$

The system is solved by dense Cholesky (n = 91). Three priors are implemented:

| L | Penalises | Null space |
|---|---|---|
| I | ‖q‖² | — |
| ∇<sub>h</sub> (first differences, x and y) | ‖∇q‖² — default | constants (uniform Joule heat is not penalised) |
| Δ<sub>h</sub> (five-point, mirrored edges) | curvature | constants |

λ is reported as a **relative** value λ<sub>rel</sub> = λ / (tr(AᵀA)/tr(LᵀL)). This makes it scale-free across priors and time.

### 6.6 Choosing λ

**Generalised cross-validation (default).** The influence matrix H<sub>λ</sub> = A(AᵀA + λLᵀL)⁻¹Aᵀ has the same trace as (AᵀA + λLᵀL)⁻¹AᵀA, which is computable from the accumulated normal equations with one Cholesky factorisation and n solves:

$$
\mathrm{GCV}(\lambda) = \frac{m\,\lVert A\mathbf q_\lambda - \mathbf d\rVert^2}{(m - \operatorname{tr} H_\lambda)^2}.
$$

The minimum is bracketed on a 28-point logarithmic grid and refined by golden-section search on log λ.

**L-curve.** The curve (log‖Aq<sub>λ</sub> − d‖, log‖Lq<sub>λ</sub>‖) is traced over λ. The corner is the point of maximum discrete **Menger curvature** κ = 4·Area/(|ab||bc||ca|) over consecutive triples.

**Discrepancy principle.** Choose λ so that ‖Aq<sub>λ</sub> − d‖² = τ m σ² (bisection, since the residual is monotone in λ). It needs the total data error to be dominated by known noise. In ThermoTwin the plant's finer grid adds model error, the target is unattainable, and the rule degenerates. This is documented in the experiments as a real limitation of the method.

### 6.7 State estimate and hotspot

The full field is T̂ = T₀ + Σ q̂<sub>j</sub>T<sub>j</sub>, refreshed every step for free between inverse solves. Hotspot detection (`Analysis/HotspotDetector.cs`) works on q̂:

1. the background b is the median of the field (robust to the anomaly);
2. the threshold is τ = b + ½(max − b);
3. the connected region above τ that contains the maximum is found by flood fill;
4. the hotspot is the centroid of that region weighted by the excess (q̂ − τ). Its area and prominence are reported.

---

## 7. Prediction (`Prediction/ThermalPredictor.cs`)

The forecast integrates the same Crank–Nicolson model forward from (T̂, q̂) with the known future load s(t) and a candidate piecewise-constant cooling plan u(t). The controller uses a 10 s step. Two forecasts are kept: under the baseline cooling ("no action", which drives the risk classification) and under the optimal plan.

**Risk policy** (`Domain/Services/ThermalRiskPolicy.cs`): *Critical* if the current estimate ≥ T<sub>safe</sub> or the forecast ≥ T<sub>critical</sub>; *Warning* if the forecast ≥ T<sub>safe</sub>; *Elevated* within 5 K of T<sub>safe</sub>; otherwise *Normal*.

---

## 8. Constrained cooling optimisation (`Optimization/CoolingOptimizer.cs`)

### Problem

Decision variables: u = (u₁, …, u<sub>K</sub>) ∈ [0, 1]<sup>K</sup>, one level per segment of length Δt<sub>s</sub>.

$$
\min_{\mathbf u}\ J(\mathbf u) = \frac{1}{E_{ref}}\sum_k P_{rated}\,u_k^3\,\Delta t_s + w\sum_k (u_{k+1}-u_k)^2
\quad\text{s.t.}\quad \max_x T(x,t_n;\mathbf u) \le T_{safe}\ \ \forall n,\quad 0\le u_k\le 1 .
$$

The cubic pump law (fan/pump affinity law) makes the energy term strictly convex, so spreading cooling over time is cheaper than bursts. The map u ↦ T is **nonlinear** because the sink H(u)(T − T<sub>c</sub>) is bilinear.

### Exterior penalty method

$$
\Phi_\mu(\mathbf u) = J(\mathbf u) + \mu\,\frac{1}{N}\sum_n \max\bigl(0,\ T_{max}(t_n;\mathbf u) - T_{safe}\bigr)^2, \qquad \mu = 10 \to 300 \to 10^4 .
$$

Each stage is warm-started from the previous one (continuation).

### Projected gradient with Armijo backtracking

$$
\mathbf u^{k+1} = P_{[0,1]^K}\bigl(\mathbf u^k - s_k\nabla\Phi_\mu(\mathbf u^k)\bigr),
\qquad \Phi_\mu(\mathbf u^{k+1}) \le \Phi_\mu(\mathbf u^k) - 10^{-4}\,\nabla\Phi_\mu^\top(\mathbf u^k - \mathbf u^{k+1}).
$$

The initial step is s = 0.5/‖∇Φ‖<sub>∞</sub>, so at most half the box per iteration, halved until the Armijo condition holds. ∇Φ comes from one-sided finite differences, h = 10⁻³ (inward at the bounds). That costs K forward PDE solves per iteration, run with `Parallel.For`.

### Feasibility repair and baselines

An exterior penalty can leave a small violation (O(1/μ)). If the final plan still violates T<sub>safe</sub> by more than 0.02 K, all levels are raised by a uniform δ found by bisection. For comparison, `MinimumFeasibleConstantLevel` finds the cheapest constant level by bisection.

### Model-predictive control

In the live twin, every 60 s the problem is solved on a 6 × 100 s horizon from the latest (T̂, q̂). The first segment is applied and the shifted plan warm-starts the next solve. The optimiser targets T<sub>safe</sub> − 1 K, a back-off that absorbs the twin's ~1 K under-estimation of the peak (Section 6.7 and the forecast experiment).

---

## 9. Avoiding the inverse crime (`Simulation/BatteryPlant.cs`)

Synthetic data generated by the same discretisation used for inversion makes reconstructions look unrealistically good. The plant therefore differs from the twin's model in three ways:

- it runs on a grid refined 2× (80×40);
- it uses the exact Gaussian defect, which is not representable in the hat basis;
- it adds Gaussian sensor noise.

Fields are compared on the model grid after finite-volume restriction (cell averaging).

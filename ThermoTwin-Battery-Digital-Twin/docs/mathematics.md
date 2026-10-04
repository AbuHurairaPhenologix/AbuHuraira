# Mathematics of ThermoTwin.NET

This document derives every algorithm implemented in `src/backend/ThermoTwin.Numerics` and states the mathematical facts the implementation relies on. Each section names the file that implements it. Numerical *results* are not quoted here; they are in [experiments.md](experiments.md) and [`docs/results/`](results/summary.md). The only numbers below are physical constants of the demo cell and quantities derived directly from them.

| Part | Content |
|---|---|
| [I. Model](#part-i--the-mathematical-model) | governing equation, boundary and initial conditions, sources |
| [II. Analysis](#part-ii--mathematical-analysis-of-the-thermal-model) | PDE classification, weak formulation, dimensionless groups, energy estimate, ill-posedness of the inverse problem |
| [III. Discretisation](#part-iii--discretisation) | finite volumes, finite elements, θ-method, linear algebra, verification problems |
| [IV. Inverse problem](#part-iv--inverse-problem-and-regularisation) | superposition, Tikhonov, GCV, L-curve, discrepancy, identifiability |
| [V. Reduced-order modelling](#part-v--reduced-order-modelling-pod) | POD, method of snapshots, Galerkin projection |
| [VI. Optimal control](#part-vi--pde-constrained-optimisation) | PDE-constrained formulation, discrete adjoint, KKT conditions, MPC |

---

# Part I — The mathematical model

## 1. Governing equation (`Physics/ThermalModel.cs`)

A large-format pouch cell is thin (δ = 10 mm) compared with its in-plane size (200 × 100 mm). The temperature is therefore averaged through the thickness. Integrating the 3-D heat equation over the thickness, with a cold plate on one face (heat-transfer coefficient h(u)), gives the depth-averaged balance on Ω = (0, L<sub>x</sub>) × (0, L<sub>y</sub>):

$$
\rho c_p \frac{\partial T}{\partial t} = \nabla\cdot(k\nabla T) + q(\mathbf x,t) - H(u)\,(T - T_c), \qquad H(u) = \frac{h(u)}{\delta},\qquad h(u) = h_{min} + u\,(h_{max}-h_{min}).
$$

| Symbol | Unit | Meaning | Demo |
|---|---|---|---|
| T | °C | depth-averaged temperature | — |
| ρ, c<sub>p</sub> | kg/m³, J/(kg·K) | density, specific heat | 2500, 1000 |
| k | W/(m·K) | effective in-plane conductivity of the electrode stack | 20 |
| q | W/m³ | volumetric heat generation (Joule heat + defect) | unknown to the twin |
| u ∈ [0, 1] | – | cooling control (pump level) | decision variable |
| h(u) | W/(m²·K) | cold-plate coefficient | 5 … 120 |
| T<sub>c</sub> | °C | coolant temperature | 22 |

The diffusivity is α = k/(ρc<sub>p</sub>) = 8·10⁻⁶ m²/s. In what follows σ(u) = H(u)/(ρc<sub>p</sub>) [1/s] denotes the sink rate. Since h is affine in u, σ′ = (h<sub>max</sub> − h<sub>min</sub>)/(δρc<sub>p</sub>) is constant.

## 2. Boundary and initial conditions

On each edge with outward unit normal **n**:

| Kind | Condition | Physical meaning | Used |
|---|---|---|---|
| **Dirichlet** | T = T<sub>b</sub> | temperature prescribed (perfect contact with a reservoir) | verification problem |
| **Neumann** | −k ∂T/∂n = g | heat flux prescribed; g = 0 is an insulated edge | energy-conservation and manufactured tests |
| **Robin** | −k ∂T/∂n = h<sub>e</sub>(T − T<sub>∞</sub>) | Newton cooling to ambient air | demo cell (h<sub>e</sub> = 10 W/(m²·K)) |

The initial condition is T(**x**, 0) = T<sub>0</sub>(**x**) (25 °C in the demo). The Robin condition interpolates between the other two. h<sub>e</sub> → 0 gives an insulated edge; h<sub>e</sub> → ∞ gives T = T<sub>∞</sub>. The finite-volume closure keeps this property exactly (§10.2).

## 3. Heat sources and load (`Physics/HeatSource.cs`)

Ohmic heating is resistive, so the bulk Joule heat and the extra heat of a high-resistance defect both scale with the square of the current:

$$
q(\mathbf x,t) = s(t)\,\hat q(\mathbf x), \qquad \hat q(\mathbf x) = q_J + \sum_m Q_m\, e^{-\lVert \mathbf x-\mathbf x_m\rVert^2/2\sigma_m^2}, \qquad s(t) = \bigl(I(t)/I_{ref}\bigr)^2 .
$$

The battery management system measures I(t), so **s(t) is known**; the spatial shape q̂ is not. The demo uses a CC–CV fast-charge profile.

---

# Part II — Mathematical analysis of the thermal model

## 4. Classification: a linear parabolic equation

Write the equation as ρc<sub>p</sub>T<sub>t</sub> − k(T<sub>xx</sub> + T<sub>yy</sub>) + H(u)T = f. The second-order spatial part has the principal symbol k(ξ<sub>1</sub>² + ξ<sub>2</sub>²) > 0 for every ξ ≠ 0, so the operator −k∇² is uniformly **elliptic**. The equation is first order in time. It is therefore **parabolic** (heat-equation type). Three properties of parabolic problems shape the whole project:

1. **Well-posedness forward in time.** With T<sub>0</sub> ∈ L²(Ω), q ∈ L²(0, τ; L²(Ω)) and any of the boundary conditions above, there is a unique weak solution T ∈ L²(0, τ; H¹(Ω)) ∩ C([0, τ]; L²(Ω)) that depends continuously on the data (Lions–Magenes; see §5).
2. **Smoothing.** Each Fourier/eigen-mode with eigenvalue λ<sub>j</sub> of −α∇² decays like e<sup>−λ<sub>j</sub>t</sup>. Fine spatial detail (large λ<sub>j</sub>) is damped exponentially fast. This makes forward simulation robust, and it makes the inverse problem ill-posed (§9).
3. **Maximum (comparison) principle.** If two solutions have ordered data (initial values, sources, boundary data), they stay ordered. In particular, more cooling cannot raise the temperature anywhere while T ≥ T<sub>c</sub>. Monotone discrete schemes (implicit Euler, explicit Euler below its limit) inherit this; Crank–Nicolson only conditionally.

Time enters only through the first derivative, so the problem is an **initial–boundary value problem**. It needs one initial condition and one boundary condition on each edge, unlike the hyperbolic wave equation, which would need two initial conditions.

The control enters **bilinearly**, through the term H(u)T. For a fixed schedule u(t) the PDE is linear in T. The control-to-state map u ↦ T is nonlinear, but smooth (§21).

## 5. Weak (variational) formulation

Let V = H¹(Ω) (Robin/Neumann edges) or V<sub>0</sub> = {v ∈ H¹(Ω): v = 0 on Γ<sub>D</sub>} (Dirichlet edges). Multiply the strong form by a test function v ∈ V<sub>0</sub>, integrate over Ω and apply Green's formula to the diffusion term:

$$
-\int_\Omega \nabla\cdot(k\nabla T)\,v\,d\mathbf x = \int_\Omega k\nabla T\cdot\nabla v\,d\mathbf x - \int_{\partial\Omega} k\,\frac{\partial T}{\partial n}\,v\,ds .
$$

Substituting the boundary conditions, −k∂T/∂n = h<sub>e</sub>(T − T<sub>∞</sub>) on Γ<sub>R</sub> and −k∂T/∂n = g on Γ<sub>N</sub>, with v = 0 on Γ<sub>D</sub>, gives the **weak formulation**:

> Find T(t) ∈ V with T = T<sub>b</sub> on Γ<sub>D</sub> and T(0) = T<sub>0</sub> such that, for all v ∈ V<sub>0</sub> and almost every t,
>
> $$
> \int_\Omega \rho c_p\,\partial_t T\,v
> \;+\; \underbrace{\int_\Omega k\nabla T\cdot\nabla v \;+\; \int_\Omega H(u)\,T\,v \;+\; \int_{\Gamma_R} h_e\,T\,v}_{a_u(T,\,v)}
> \;=\; \underbrace{\int_\Omega q\,v \;+\; \int_\Omega H(u)\,T_c\,v \;+\; \int_{\Gamma_R} h_e T_\infty v \;-\; \int_{\Gamma_N} g\,v}_{\ell_u(v)} .
> $$

**Coercivity.** For v ∈ V<sub>0</sub>,

$$
a_u(v,v) = k\lVert\nabla v\rVert_{L^2}^2 + H(u)\lVert v\rVert_{L^2}^2 + h_e\lVert v\rVert_{L^2(\Gamma_R)}^2 \;\ge\; \min\bigl(k,\; H(u_{min})\bigr)\,\lVert v\rVert_{H^1}^2 ,
$$

and H(u) ≥ h<sub>min</sub>/δ > 0. The cold plate therefore makes a<sub>u</sub> coercive on all of H¹ even without Dirichlet edges. Lax–Milgram then gives a unique solution of every implicit time step (the "elliptic problem" (ρc<sub>p</sub>/Δt)T + a<sub>u</sub>(T, ·) = …). Galerkin/energy methods give existence and uniqueness for the parabolic problem. The same coercivity makes the discrete θ-step matrices symmetric positive definite (§11).

## 6. Semi-discrete systems (method of lines)

Both spatial discretisations turn the PDE into a linear ODE system for the vector **T**(t) of unknowns.

**Finite volumes** (cell averages, `Pde/`): with the discrete Laplacian ∇²T ≈ A**T** + **b**,

$$
\frac{d\mathbf T}{dt} = M(u)\,\mathbf T + \mathbf f(t,u), \qquad
M(u) = \alpha A - \sigma(u) I, \qquad
\mathbf f = \alpha\,\mathbf b + \frac{\mathbf q}{\rho c_p} + \sigma(u)\,T_c\,\mathbb 1 .
$$

**Finite elements** (nodal values, `Fem/`): with T<sub>h</sub> = Σ T<sub>j</sub>(t)φ<sub>j</sub> and v = φ<sub>i</sub>,

$$
\rho c_p\,M\,\dot{\mathbf T} + \bigl(K + R + H(u)\,M\bigr)\,\mathbf T = M\,\mathbf q + H(u)\,T_c\,M\mathbb 1 + \mathbf r ,
$$

where M<sub>ij</sub> = ∫φ<sub>i</sub>φ<sub>j</sub> (mass), K<sub>ij</sub> = ∫k∇φ<sub>i</sub>·∇φ<sub>j</sub> (stiffness), R<sub>ij</sub> = ∫<sub>Γ<sub>R</sub></sub>h<sub>e</sub>φ<sub>i</sub>φ<sub>j</sub> (Robin boundary mass) and r collects the boundary data. A, K, M and R are **symmetric**. M is positive definite, and K + R + HM is positive definite. The finite-volume operator is the FEM system with a diagonal ("lumped") mass and a different stiffness; the two are genuinely different discretisations of the same weak problem.

## 7. Dimensionless form and characteristic numbers

Scale with the short side L = L<sub>y</sub>, the diffusion time τ = L²/α and a temperature rise ΔT<sub>ref</sub>:

$$
\mathbf x^* = \mathbf x/L,\quad t^* = t/\tau,\quad \vartheta = \frac{T - T_c}{\Delta T_{ref}}
\;\;\Longrightarrow\;\;
\frac{\partial\vartheta}{\partial t^*} = \nabla^{*2}\vartheta + \Pi_q(\mathbf x^*,t^*) - \Gamma(u)\,\vartheta,
\qquad -\frac{\partial\vartheta}{\partial n^*} = \mathrm{Bi}_e\,(\vartheta - \vartheta_\infty),
$$

with Π<sub>q</sub> = qL²/(kΔT<sub>ref</sub>). Only a few groups matter physically:

| Group | Definition | Demo value | Meaning |
|---|---|---:|---|
| diffusion time | τ = L<sub>y</sub>²/α | 1250 s | time for heat to cross the cell |
| Fourier number of the charge | Fo = α t<sub>end</sub>/L<sub>y</sub>² | 1.44 | the defect's heat spreads across the cell during one charge: visible to sensors, but smoothed |
| edge Biot number | Bi<sub>e</sub> = h<sub>e</sub>L<sub>y</sub>/k | 0.05 | edges barely cool; ≪ 1 means almost insulated |
| cooling number | Γ(u) = h(u)L<sub>y</sub>²/(δk) | 0.25 … 6.0 | cold-plate removal rate relative to in-plane diffusion |
| grid Fourier number | r = αΔt(Δx⁻² + Δy⁻²) | 3.2 (40×20, Δt = 5 s) | > ½ ⇒ explicit Euler unstable at the demo step |

Γ(1)/Bi<sub>e</sub> ≈ 120: the cold plate, not the edges, controls the temperature. That is why cooling is a meaningful control. Γ(u) of order one also explains why the problem is not trivially "lumped": diffusion and cooling compete on the same time scale.

*Implementation:* these groups are computed from the scenario on the dashboard's *Mathematical Model* page.

## 8. Energy estimate and stability

Let w = T − T<sub>c</sub> and, for clarity, T<sub>∞</sub> = T<sub>c</sub> (the general case adds a bounded forcing term). Taking v = w in the weak form gives the energy identity

$$
\frac{d}{dt}\Bigl(\frac{\rho c_p}{2}\lVert w\rVert^2\Bigr) + k\lVert\nabla w\rVert^2 + H(u)\lVert w\rVert^2 + h_e\lVert w\rVert^2_{\Gamma_R} = (q, w).
$$

With Young's inequality, (q, w) ≤ ‖q‖²/(2H) + (H/2)‖w‖²:

$$
\frac{d}{dt}\lVert w\rVert^2 + \frac{H(u)}{\rho c_p}\lVert w\rVert^2 \le \frac{\lVert q\rVert^2}{\rho c_p\,H(u)}
\;\;\Longrightarrow\;\;
\lVert w(t)\rVert^2 \le e^{-\sigma t}\lVert w(0)\rVert^2 + \frac{1 - e^{-\sigma t}}{\sigma^2}\,\frac{\sup_s \lVert q(s)\rVert^2}{(\rho c_p)^2} .
$$

Physically: the excess thermal energy decays at least at the cooling rate σ(u). With bounded heating it stays bounded by sup‖q‖/(ρc<sub>p</sub>σ). Stronger cooling (larger σ) shrinks the bound. That is the mechanism the optimiser exploits.

**Discrete analogue.** For the θ-scheme with symmetric negative-definite M, every eigen-mode is multiplied per step by G(z) = (1 + (1−θ)z)/(1 − θz), z = Δtλ ≤ 0. |G| ≤ 1 for all z ≤ 0 iff θ ≥ ½ (unconditional stability). For θ = 0, stability holds iff Δt ≤ 2/ρ(M) (§11.3). For Crank–Nicolson, G(z) → −1 as z → −∞: stiff modes are not damped (A-stable, not L-stable).

**Conservation.** With insulated edges and no cooling, summing the finite-volume equations over all cells cancels every interior flux exactly. The discrete energy balance Σρc<sub>p</sub>ΔT·V = Δt Σ q̄ V then holds to round-off for every θ. For P1 elements the same holds because K𝟙 = 0 (constants are in the kernel of the stiffness): 𝟙ᵀ(ρc<sub>p</sub>M Ṫ) = 𝟙ᵀMq.

## 9. Why the inverse problem is ill-posed

The twin must recover q̂ from y = C T(q̂) + ε, where C samples 12 sensors. In eigen-coordinates of the (self-adjoint) diffusion operator, a source mode φ<sub>j</sub> with eigenvalue λ<sub>j</sub> produces a temperature response attenuated roughly like

$$
T_j(t) \;\propto\; \frac{1 - e^{-(\lambda_j + \sigma)t}}{\lambda_j + \sigma}\;\varphi_j ,
$$

so the forward map q̂ ↦ y is a **smoothing (compact) operator**. Its singular values σ<sub>j</sub> decay with spatial frequency. Hadamard's three conditions fail:

- **Existence:** the plant is not the model (finer grid, exact Gaussian), so noisy data are generally not in the range of the model operator.
- **Uniqueness:** 12 sensors × N times cannot determine an arbitrary field. Without the finite basis (91 hats) the null space is infinite-dimensional.
- **Stability:** the least-squares solution Σ<sub>j</sub> (u<sub>j</sub>ᵀy/σ<sub>j</sub>)v<sub>j</sub> divides noise by tiny σ<sub>j</sub>. The *discrete Picard condition* (|u<sub>j</sub>ᵀy| decaying faster than σ<sub>j</sub>) fails once the noise floor is reached.

The remedy is regularisation (§13). The same smoothing that makes reconstruction of q̂ hard makes the **temperature** reconstruction easy: diffusion is insensitive to exactly the details that cannot be recovered.

---

# Part III — Discretisation

## 10. Finite-volume method (`Pde/DiscreteLaplacian.cs`)

### 10.1 Five-point flux balance

The grid is cell-centred: cell (i, j) has its centre at ((i + ½)Δx, (j + ½)Δy). Integrating ∇·(k∇T) over a cell and approximating each face flux by a centred difference gives the five-point Laplacian

$$
(\nabla^2 T)_{ij} \approx \frac{T_{i+1,j} - 2T_{ij} + T_{i-1,j}}{\Delta x^2} + \frac{T_{i,j+1} - 2T_{ij} + T_{i,j-1}}{\Delta y^2},
$$

which is second-order consistent and exactly conservative: the flux leaving one cell enters its neighbour.

### 10.2 Ghost-cell boundary closures

A boundary face lies h/2 from the adjacent cell centre P. A ghost value T<sub>g</sub> is defined so that the stencil above still applies:

- **Dirichlet:** the face value (T<sub>P</sub> + T<sub>g</sub>)/2 = T<sub>b</sub> ⇒ T<sub>g</sub> = 2T<sub>b</sub> − T<sub>P</sub>. Contribution −2T<sub>P</sub>/h² + 2T<sub>b</sub>/h².
- **Neumann:** −k(T<sub>g</sub> − T<sub>P</sub>)/h = g ⇒ contribution −g/(kh).
- **Robin:** −k(T<sub>g</sub> − T<sub>P</sub>)/h = h<sub>e</sub>((T<sub>P</sub> + T<sub>g</sub>)/2 − T<sub>∞</sub>). With β = h<sub>e</sub>h/k:

$$
\frac{T_g - T_P}{h^2} = \frac{\gamma\,(T_\infty - T_P)}{h^2}, \qquad \gamma = \frac{\beta}{1 + \beta/2}.
$$

  As h<sub>e</sub> → ∞, γ → 2 (Dirichlet); as h<sub>e</sub> → 0, γ → 0 (insulated).

The result is ∇²T ≈ A**T** + **b**, A **symmetric** and negative (semi-)definite.

### 10.3 Ordering and bandwidth

Unknowns are numbered k = i·N<sub>y</sub> + j (short side fastest), so the half-bandwidth is p = N<sub>y</sub>.

## 11. Finite-element method (`Fem/`)

### 11.1 Mesh and P1 space (`FemMesh2D.cs`)

The N<sub>x</sub> × N<sub>y</sub> rectangles of the finite-volume grid are each split along their south-west → north-east diagonal into two counter-clockwise triangles, so both methods share the mesh size h. V<sub>h</sub> is the space of continuous functions that are linear on each triangle, with the hat basis φ<sub>i</sub>(**x**<sub>j</sub>) = δ<sub>ij</sub>. Nodes are numbered k = i(N<sub>y</sub> + 1) + j. A node couples to six neighbours, so the matrices have ≤ 7 non-zeros per row and half-bandwidth N<sub>y</sub> + 2.

### 11.2 Element integrals (`TriangleElement`)

With the affine map **x** = **x**<sub>a</sub> + Jξ from the reference triangle, J = [**x**<sub>b</sub> − **x**<sub>a</sub>, **x**<sub>c</sub> − **x**<sub>a</sub>], |T| = det J/2 (positive for counter-clockwise vertices; clockwise or degenerate triangles are rejected). The barycentric gradients are constant:

$$
\nabla\varphi_a = \frac{(y_b - y_c,\; x_c - x_b)}{\det J},\quad
\nabla\varphi_b = \frac{(y_c - y_a,\; x_a - x_c)}{\det J},\quad
\nabla\varphi_c = \frac{(y_a - y_b,\; x_b - x_a)}{\det J},
$$

and they sum to zero. The element matrices are therefore integrated exactly:

$$
K^e_{ij} = k\,|T|\,\nabla\varphi_i\cdot\nabla\varphi_j, \qquad
M^e_{ij} = \frac{|T|}{12}(1 + \delta_{ij}), \qquad
\int_e h_e\varphi_a\varphi_b\,ds = \frac{h_e\ell}{6}\begin{pmatrix}2&1\\1&2\end{pmatrix}.
$$

K<sup>e</sup> has zero row sums (constants are in its kernel) and is positive semi-definite. M<sup>e</sup> is SPD with Σ<sub>ij</sub>M<sup>e</sup><sub>ij</sub> = |T|.

### 11.3 Assembly and boundary conditions (`FemAssembler.cs`)

Global matrices are assembled by scattering element contributions into a CSR builder. Robin edges add R and r = ∫h<sub>e</sub>T<sub>∞</sub>φ<sub>i</sub>; Neumann edges add −∫gφ<sub>i</sub>. Dirichlet nodes are **eliminated symmetrically**: their rows and columns of the system matrix are replaced by the identity, and the known values are moved to the right-hand side (rhs<sub>free</sub> −= A<sub>free,D</sub> g<sub>D</sub>). This keeps the matrix SPD and lets the banded Cholesky factor be reused for every step. The source is represented by its nodal interpolant, ∫qφ<sub>i</sub> ≈ (M**q**)<sub>i</sub>, which is second-order accurate.

### 11.4 Error norms (`FemErrorNorms.cs`)

Verification measures the continuous norms ‖T − T<sub>h</sub>‖<sub>L²(Ω)</sub> and |T − T<sub>h</sub>|<sub>H¹(Ω)</sub>, using the 6-point degree-4 Dunavant quadrature on each triangle. For smooth solutions, standard P1 theory (Céa's lemma, interpolation estimates and the Aubin–Nitsche duality argument) predicts O(h) in H¹ and O(h²) in L².

## 12. Time integration (`Pde/HeatEquationSolver.cs`, `Fem/FemHeatEquationSolver.cs`)

### 12.1 θ-method

For the FVM system:

$$
\underbrace{(I - \theta\Delta t\,M)}_{L(u)}\,\mathbf T^{n+1} = \underbrace{(I + (1-\theta)\Delta t\,M)}_{R(u)}\,\mathbf T^{n} + \Delta t\,[\theta\,\mathbf f^{n+1} + (1-\theta)\,\mathbf f^{n}] .
$$

For the FEM system, M is replaced by the mass/stiffness pencil: [ρc<sub>p</sub>M + θΔtK<sub>t</sub>]**T**<sup>n+1</sup> = [ρc<sub>p</sub>M − (1−θ)ΔtK<sub>t</sub>]**T**<sup>n</sup> + Δt[θ**F**<sup>n+1</sup> + (1−θ)**F**<sup>n</sup>].

| θ | Scheme | Local truncation | Order | Stability |
|---|---|---|---|---|
| 0 | explicit Euler | O(Δt²) | 1 | Δt ≤ 2/ρ(M) |
| 1 | implicit Euler | O(Δt²) | 1 | unconditional, L-stable |
| ½ | Crank–Nicolson | O(Δt³) | 2 | unconditional, A-stable (not L-stable) |

**Consistency + stability ⇒ convergence** (Lax–Richtmyer equivalence for linear problems): the global error is O(h² + Δt<sup>p</sup>). Verification therefore refines h with Δt ∝ h² (any θ) or Δt ∝ h (Crank–Nicolson), so that both error components have the same order.

### 12.2 Banded Cholesky (`LinearAlgebra/BandedCholesky.cs`)

L(u) is symmetric positive definite. Its lower band is factorised once per cooling level as L = 𝓛𝓛ᵀ:

$$
l_{jj} = \sqrt{s_{jj} - \textstyle\sum_{k=j-p}^{j-1} l_{jk}^2}, \qquad
l_{ij} = \Bigl(s_{ij} - \textstyle\sum_{k=i-p}^{j-1} l_{ik}l_{jk}\Bigr)/l_{jj}, \quad j < i \le j+p.
$$

That costs O(np²) to factor and O(np) per solve. Factors are cached per cooling level in a thread-safe, size-bounded dictionary; an optimiser visits a continuum of levels, so the cache is flushed when it exceeds its bound. A conjugate-gradient solver independently cross-checks the implicit step in the tests.

### 12.3 Stability analysis (`Pde/StabilityAnalyzer.cs`)

M is symmetric negative definite with spectrum in [−ρ(M), 0). ρ(M) is estimated by power iteration from a checkerboard start vector and bounded above by Gershgorin's theorem, ρ(M) ≤ max<sub>i</sub>Σ<sub>j</sub>|m<sub>ij</sub>|. For the interior stencil this reproduces the textbook bound Δt ≤ 1/(2α(Δx⁻² + Δy⁻²)).

## 13. Verification problems (`Validation/ConvergenceStudy.cs`, `Validation/FemVerificationStudy.cs`)

1. **Dirichlet eigenmode.** T = T<sub>b</sub> + A e<sup>−απ²(L<sub>x</sub>⁻² + L<sub>y</sub>⁻²)t</sup> sin(πx/L<sub>x</sub>) sin(πy/L<sub>y</sub>). The sampled mode is an exact eigenvector of the ghost-cell finite-volume operator, with eigenvalue μ<sub>h</sub> = α[(2cos(πΔx/L<sub>x</sub>) − 2)/Δx² + (2cos(πΔy/L<sub>y</sub>) − 2)/Δy²]. That gives a second reference, the semi-discrete solution e<sup>μ<sub>h</sub>t</sup>**v**, which isolates the time-integration error.
2. **Manufactured solution with every model term.** On an insulated cell with cooling level u,

$$
T = T_c + \beta + \gamma(t)\cos\frac{\pi x}{L_x}\cos\frac{2\pi y}{L_y}, \quad \gamma(t) = A(1 - e^{-t/\tau}),
\qquad
q = \rho c_p\gamma'\varphi + (k\kappa^2 + H)\gamma\varphi + H\beta, \quad \kappa^2 = \frac{\pi^2}{L_x^2} + \frac{4\pi^2}{L_y^2},
$$

   so the mass, stiffness, sink and time-dependent source are all exercised against a known answer.
3. **Steady Robin problem** (unit test): −kT″ = q with Robin ends has the exact solution T = T<sub>∞</sub> + qL/(2h) + q(xL − x²)/(2k). This checks the Robin boundary terms directly.
4. **FVM ↔ FEM cross-validation** on the full battery model (Robin edges, sink, Gaussian defect, CC–CV load). No exact solution exists. Agreement between two independent discretisations, and the O(h²) decay of their difference, is the evidence.

The observed order is p = log(e<sub>coarse</sub>/e<sub>fine</sub>)/log(h<sub>coarse</sub>/h<sub>fine</sub>).

---

# Part IV — Inverse problem and regularisation

## 14. Sensors and noise (`Sensors/SensorNetwork.cs`)

Each thermistor reads the bilinear interpolation of the four surrounding cell centres, so y = C**T** with C a sparse 12 × 800 matrix. The reading is exact for linear fields. Noise is ε ~ N(0, σ²I) from a seeded Box–Muller generator, so every experiment is reproducible.

## 15. Linear superposition and the measurement equation (`Inverse/`)

The source is expanded in bilinear hat functions on a coarse 13 × 7 lattice (16.7 mm spacing): q = s(t)Σ<sub>j</sub>q<sub>j</sub>φ<sub>j</sub>. Reducing 800 grid values to 91 coefficients is the first, implicit, regularisation. Linearity in (T, q) gives

$$
\mathbf T(t) = \mathbf T_0(t) + \sum_j q_j\,\mathbf T_j(t), \qquad
\underbrace{\mathbf y - C\,\mathbf T_0}_{\mathbf d} = A\,\mathbf q + \boldsymbol\varepsilon, \quad A_{(t,s),j} = (C\,\mathbf T_j(t))_s ,
$$

with **T**<sub>0</sub> the response to the known data and **T**<sub>j</sub> the homogeneous response to s(t)φ<sub>j</sub> under the actual cooling history. The estimator never stores A. It accumulates AᵀA += **a**<sub>s</sub>**a**<sub>s</sub>ᵀ, Aᵀ**d** += d<sub>s</sub>**a**<sub>s</sub> and **d**ᵀ**d** recursively, so the cost per step does not grow with the history.

## 16. Tikhonov regularisation and parameter choice

$$
\mathbf q_\lambda = \arg\min_{\mathbf q}\ \lVert A\mathbf q - \mathbf d\rVert^2 + \lambda\lVert L\mathbf q\rVert^2
\quad\Longleftrightarrow\quad
(A^\top A + \lambda L^\top L)\,\mathbf q_\lambda = A^\top\mathbf d .
$$

In the generalised SVD of (A, L), Tikhonov multiplies each component by a **filter factor** f<sub>i</sub> = γ<sub>i</sub>²/(γ<sub>i</sub>² + λ). Components with γ<sub>i</sub>² ≫ λ pass; noise-dominated components with γ<sub>i</sub>² ≪ λ are suppressed. L ∈ {I, ∇<sub>h</sub>, Δ<sub>h</sub>}; the default ∇<sub>h</sub> leaves the uniform Joule heat unpenalised. λ is reported relative to tr(AᵀA)/tr(LᵀL).

- **GCV (default):** minimise m‖A**q**<sub>λ</sub> − **d**‖²/(m − tr H<sub>λ</sub>)². tr H<sub>λ</sub> = tr((AᵀA + λLᵀL)⁻¹AᵀA) is the effective number of resolved parameters, computed from one Cholesky factorisation and n triangular solves.
- **L-curve:** corner of maximum Menger curvature of (log‖A**q**<sub>λ</sub> − **d**‖, log‖L**q**<sub>λ</sub>‖).
- **Discrepancy principle:** ‖A**q**<sub>λ</sub> − **d**‖² = τmσ². It assumes the data error is pure sensor noise. With model error from the finer plant grid the target can be unattainable, and the rule then degenerates. This is reported as a limitation, not hidden.

The state estimate is **T̂** = **T**<sub>0</sub> + Σq̂<sub>j</sub>**T**<sub>j</sub>. Hotspots are found on q̂ by a median background, a half-maximum threshold and a flood-filled, excess-weighted centroid.

## 17. Parameter sensitivity and practical identifiability (`Sensitivity/`)

The inverse problem above assumes k and h<sub>e</sub> are known. The identifiability study asks whether the sensors can separate the defect (Q, x<sub>0</sub>, y<sub>0</sub>) from uncertainty in k and h<sub>e</sub>. For the parameter-to-observation map θ ↦ **y**(θ) ∈ ℝ<sup>m</sup>:

$$
S = \frac{\partial\mathbf y}{\partial\theta}\Big|_{\theta_0}\ (\text{central differences}),\qquad
\tilde S = S\,\mathrm{diag}(\delta\theta),\qquad
F = \frac{S^\top S}{\sigma^2},\qquad
\operatorname{Cov}(\hat\theta) \succeq F^{-1}\ (\text{Cramér–Rao}).
$$

- **Column norms** ‖S̃<sub>j</sub>‖/√m give the RMS sensor response to a one-δθ change. Compared with σ they give a signal-to-noise ratio.
- **Collinearity:** cos∠(S<sub>i</sub>, S<sub>j</sub>) near ±1 means one parameter's effect can be mimicked by another. The collinearity index γ<sub>K</sub> = 1/√λ<sub>min</sub>(S̃<sub>K</sub>ᵀS̃<sub>K</sub>) (unit-norm columns) summarises a subset K; values above ≈ 10–15 indicate practical non-identifiability (Brun, Reichert & Künsch 2001).
- **Estimation:** box-constrained Levenberg–Marquardt, (JᵀJ + λ diag JᵀJ)Δ = −Jᵀ**r**, θ<sup>+</sup> = P<sub>[ℓ,u]</sub>(θ + Δ), in uncertainty-scaled variables. Standard errors come from (JᵀJ)⁻¹σ̂².

The analysis is **local**: it linearises at θ<sub>0</sub> and assumes the model form is correct. Local identifiability does not imply global uniqueness.

---

# Part V — Reduced-order modelling (POD)

## 18. Proper orthogonal decomposition (`ReducedOrder/PodBasis.cs`)

Given centred snapshots X = [**x**<sub>1</sub> − T̄, …, **x**<sub>m</sub> − T̄] ∈ ℝ<sup>n×m</sup>, POD finds the r-dimensional subspace that is optimal in the mean-square sense:

$$
\min_{\Phi_r^\top\Phi_r = I}\ \frac1m\sum_{j=1}^m \bigl\lVert \mathbf x_j - \bar T - \Phi_r\Phi_r^\top(\mathbf x_j - \bar T)\bigr\rVert^2 \;=\; \sum_{i>r}\lambda_i .
$$

The minimiser is spanned by the leading eigenvectors of the covariance XXᵀ/m. The **method of snapshots** (Sirovich) solves the much smaller m × m eigenproblem of C = XᵀX/m instead, Cv<sub>i</sub> = λ<sub>i</sub>v<sub>i</sub>, and recovers the modes φ<sub>i</sub> = Xv<sub>i</sub>/√(mλ<sub>i</sub>). XXᵀ and XᵀX share their non-zero spectrum: the squared singular values of X/√m. The "energy" captured by r modes is E(r) = Σ<sub>i≤r</sub>λ<sub>i</sub>/Σ<sub>i</sub>λ<sub>i</sub>. The identity above (mean projection error = discarded eigenvalues) is checked in the test-suite.

On the uniform grid the discrete L²(Ω) inner product ΔxΔy·uᵀv is a constant multiple of the Euclidean one, so the modes are identical. They are normalised Euclidean-orthonormal and re-orthogonalised by two passes of modified Gram–Schmidt.

**Eigensolvers** (`LinearAlgebra/SymmetricEigensolver.cs`). Two independent dense symmetric eigensolvers are implemented and cross-checked in the tests. The default is Householder tridiagonalisation + implicitly shifted QL (EISPACK tred2/tql2, ≈ (4/3 + 3)n³ flops including vectors). The alternative, cyclic Jacobi, is slower but simple, and accurate to high relative precision on small matrices.

## 19. Galerkin projection (`ReducedOrder/ReducedThermalModel.cs`)

Insert **T** ≈ T̄ + Φ<sub>r</sub>**a** into d**T**/dt = (αA − σ(u)I)**T** + α**b** + s(t)**q**/ρc<sub>p</sub> + σ(u)T<sub>c</sub>𝟙 and require the residual to be orthogonal to span Φ<sub>r</sub>:

$$
\dot{\mathbf a} = \bigl(\alpha A_r - \sigma(u) I_r\bigr)\mathbf a + \alpha\Phi_r^\top(A\bar T + \mathbf b) + s(t)\,\Phi_r^\top\mathbf q/\rho c_p + \sigma(u)\,\Phi_r^\top(T_c\mathbb 1 - \bar T),
\qquad A_r = \Phi_r^\top A\Phi_r .
$$

A<sub>r</sub> is symmetric negative semi-definite because A is. It is diagonalised once offline, A<sub>r</sub> = VΛVᵀ, and the model is written in the rotated orthonormal basis Ψ = Φ<sub>r</sub>V. **Because the sink acts as σ(u)·I, it commutes with the diffusion operator.** M<sub>r</sub>(u) = αΛ − σ(u)I is therefore diagonal for *every* cooling level. A θ-step costs O(r) with no factorisation, and the reduced operators are trivially symmetric for the adjoint. The same θ-scheme is used, so the ROM is the exact Galerkin projection of the *discrete* full model. With a complete basis (r = n) it reproduces the full model to round-off; this is a unit test.

**Consequences of the commuting sink.** For any schedule u(t), the solution propagator factorises as e<sup>−∫σ</sup>e<sup>αA(t−s)</sup>. The reachable states therefore lie in the span of the *diffusion orbits* e<sup>αAτ</sup>**v** of the source, boundary and initial data, whatever the cooling. The training set must vary the **source shape**, not the cooling schedule. A moving, localised defect has a slowly decaying Kolmogorov n-width: training on a few defect locations does not represent defects in between. ThermoTwin therefore trains on one response per hat function of the inverse problem's source basis (plus Joule heating). Every reconstructed q̂ = Σq<sub>j</sub>φ<sub>j</sub> then lies in the training span by construction. The experiments compare this with a naive defect-lattice training set.

**Error and cost.** For this linear, symmetric, dissipative system the Galerkin ROM error is close to the best-approximation (projection) error, which is quasi-optimality in the energy norm; the experiments report both. A ROM step costs O(r) for the dynamics plus O(nr) to reconstruct the field. The full model costs O(np) per banded solve. On a 40×20 grid the reconstruction limits the speed-up of full-field simulation. The optimiser therefore evaluates the state constraint only on a screened hot region (§23).

---

# Part VI — PDE-constrained optimisation

## 20. Formulation (`Optimization/PdeConstrainedObjective.cs`)

| Element | Definition |
|---|---|
| **State** | T(**x**, t), the solution of the heat equation (the discretised state x<sup>n</sup> ∈ ℝ<sup>n</sup> or the ROM state ∈ ℝ<sup>r</sup>) |
| **Control** | u = (u<sub>1</sub>, …, u<sub>K</sub>), piecewise constant on K segments of the horizon |
| **Objective** | J(T, u) = (1/E<sub>ref</sub>)Σ<sub>k</sub>P<sub>rated</sub>u<sub>k</sub>³Δt<sub>k</sub> + wΣ<sub>k</sub>(u<sub>k+1</sub> − u<sub>k</sub>)² (cubic pump law + smoothness) |
| **PDE state constraint** | ρc<sub>p</sub>T<sub>t</sub> − k∇²T + H(u)(T − T<sub>c</sub>) − q = 0, with boundary and initial conditions |
| **Inequality (state) constraint** | T(**x**, t) ≤ T<sub>safe</sub> for all **x**, t in the horizon (pointwise) |
| **Box constraints** | 0 ≤ u<sub>k</sub> ≤ 1 |
| **Feasible set** | U<sub>ad</sub> = [0, 1]<sup>K</sup> ∩ {u : T(u) ≤ T<sub>safe</sub>} |

Eliminating the state through the control-to-state map u ↦ T(u) gives the **reduced problem** min<sub>u∈U<sub>ad</sub></sub> Ĵ(u) = J(T(u), u). The cubic energy term is strictly convex. Spreading cooling over time is therefore cheaper than bursts.

**Moreau–Yosida relaxation of the state constraint.** Pointwise state constraints have only measure-valued multipliers in general. ThermoTwin penalises their violation on the whole space–time cylinder:

$$
\Phi_\mu(u) = J(u) + \mu\,P(u), \qquad
P(u) = \frac1N\sum_{n=1}^{N}\frac1{|\mathcal I|}\sum_{i}\max\bigl(0,\; T_i^n(u) - T_{safe}\bigr)^2 .
$$

The squared hinge is C¹ with a Lipschitz gradient, so a discrete adjoint gradient exists everywhere. μ is increased by continuation (10² → 10³ → 10⁴ → 10⁵); μ → ∞ recovers the constrained problem. Each grid point carries its own constraint, which avoids the non-smooth "max over x" of a peak-temperature penalty. *Design choice:* a log-sum-exp smooth maximum was rejected because it over-estimates the maximum by up to log(n)/β, while a peak-only penalty is not differentiable where the hottest cell changes. A uniform-shift bisection removes any O(1/μ) residual violation at the end.

## 21. Discrete adjoint (`PdeConstrainedObjective.AdjointGradient`)

Write one θ-step (FOM or ROM) as the residual

$$
e^n(x^{n+1}, x^n, u) = L(u^n)\,x^{n+1} - R(u^n)\,x^n - \Delta t\,\hat g^n(u^n) = 0,\qquad
\hat g^n = \theta g^{n+1} + (1-\theta)g^n,\quad g = c + s(t)p + \sigma(u)d ,
$$

with L = I − θΔtM(u), R = I + (1−θ)ΔtM(u), M(u) = αÃ − σ(u)I, and d = T<sub>c</sub>𝟙 (FOM) or Ψᵀ(T<sub>c</sub>𝟙 − T̄) (ROM). Form the Lagrangian

$$
\mathcal L(x, u, \lambda) = \Phi_\mu(x, u) + \sum_{n=0}^{N-1}(\lambda^{n+1})^\top e^n .
$$

**Adjoint equation.** Setting ∂𝓛/∂x<sup>n</sup> = 0 (x<sup>n</sup> appears in e<sup>n−1</sup> through L and in e<sup>n</sup> through R):

$$
L(u^{N-1})^\top\lambda^N = -\mu\,\frac{\partial P}{\partial x^N},\qquad
L(u^{n-1})^\top\lambda^n = R(u^n)^\top\lambda^{n+1} - \mu\,\frac{\partial P}{\partial x^n},\quad n = N-1,\dots,1,
$$

with ∂P/∂x<sup>n</sup> = Φᵀ·(2/(N|𝓘|))max(0, T<sup>n</sup> − T<sub>safe</sub>). This is a **backward** recursion: a terminal-value problem, the discrete analogue of the backward adjoint heat equation −ρc<sub>p</sub>λ<sub>t</sub> − k∇²λ + H(u)λ = −μ∂<sub>T</sub>P. Because Ã is symmetric, **Lᵀ = L and Rᵀ = R**. The backward sweep re-uses the forward Cholesky factors, and the transposes in the code are identities that are asserted in the tests rather than skipped.

**Gradient equation.** At a solution of the state and adjoint equations, d Φ<sub>μ</sub>/du = ∂𝓛/∂u:

$$
\frac{\partial\Phi_\mu}{\partial u_k} = \frac{\partial J}{\partial u_k} + \sum_{n\in\text{segment }k}\sigma'\,\Delta t\,(\lambda^{n+1})^\top\bigl[\theta x^{n+1} + (1-\theta)x^n - d\bigr],
\qquad \frac{\partial J}{\partial u_k} = \frac{3P_{rated}u_k^2\,\Delta t_k}{E_{ref}} + 2w(2u_k - u_{k-1} - u_{k+1}),
$$

using ∂L/∂u = θΔtσ′I, ∂R/∂u = −(1−θ)Δtσ′I and ∂ĝ/∂u = σ′d. The result is the **exact gradient of the discrete objective**: the "discretise-then-optimise" adjoint, so no consistency error between the gradient and the function being minimised.

**Cost.** One forward sweep (storing x<sup>0</sup>…x<sup>N</sup>) plus one backward sweep gives all K components. Forward finite differences need K + 1 forward solves; central differences need 2K. Finite differences are kept as the validation reference. The **Taylor test** |Φ(u + εd) − Φ(u) − ε∇Φᵀd| = O(ε²) checks the gradient independently of finite-difference step-size effects.

## 22. Optimality conditions (KKT) (`Optimization/AdjointCoolingOptimizer.cs`)

For min Φ<sub>μ</sub>(u) on the box U = [0, 1]<sup>K</sup>, the Lagrangian is Φ<sub>μ</sub>(u) − ν<sub>L</sub>ᵀu − ν<sub>U</sub>ᵀ(𝟙 − u). A local minimiser u* satisfies

$$
\nabla\Phi_\mu(u^*) = \nu_L - \nu_U,\qquad \nu_L,\nu_U \ge 0,\qquad \nu_{L,k}\,u_k^* = 0,\qquad \nu_{U,k}\,(1 - u_k^*) = 0 .
$$

Equivalently, g<sub>k</sub> ≥ 0 where u<sub>k</sub> = 0, g<sub>k</sub> ≤ 0 where u<sub>k</sub> = 1, and g<sub>k</sub> = 0 on the inactive set. Equivalently again, the **projected-gradient condition** holds:

$$
P_U\bigl(u^* - \nabla\Phi_\mu(u^*)\bigr) = u^* .
$$

The residual ‖P<sub>U</sub>(u − ∇Φ) − u‖<sub>∞</sub> is the stationarity measure reported with every solution, together with the active sets and the minimum bound multipliers. For the **state constraint**, the Moreau–Yosida term supplies the multiplier estimate η<sub>i</sub><sup>n</sup> = 2μ max(0, T<sub>i</sub><sup>n</sup> − T<sub>safe</sub>)/(N|𝓘|) ≥ 0. It is supported on the approximate active set {T ≥ T<sub>safe</sub>}, and complementarity η(T − T<sub>safe</sub>) = 0 holds up to O(1/μ). The full first-order system is

| Condition | Discrete form |
|---|---|
| state equation | e<sup>n</sup>(x<sup>n+1</sup>, x<sup>n</sup>, u) = 0, x<sup>0</sup> given |
| adjoint equation | L<sup>ᵀ</sup>λ<sup>n</sup> = R<sup>ᵀ</sup>λ<sup>n+1</sup> − μ∂P/∂x<sup>n</sup>, λ<sup>N+1</sup> = 0 |
| gradient equation | ∇Φ<sub>μ</sub>(u) − ν<sub>L</sub> + ν<sub>U</sub> = 0 |
| complementarity | ν ≥ 0, ν<sub>L</sub>·u = 0, ν<sub>U</sub>·(1 − u) = 0; η ≥ 0, η·(T − T<sub>safe</sub>) ≈ 0 |

**Algorithm.** For each μ, a projected quasi-Newton method (Bertsekas 1982) is used. With the ε-active set A = {k: u<sub>k</sub> ≤ ε, g<sub>k</sub> > 0} ∪ {k: u<sub>k</sub> ≥ 1 − ε, g<sub>k</sub> < 0} and a BFGS inverse-Hessian approximation H, the direction is d<sub>F</sub> = −H<sub>FF</sub>g<sub>F</sub> on the free set and d<sub>A</sub> = −H<sub>AA</sub>g<sub>A</sub> on the active set. Then u(α) = P<sub>U</sub>(u + αd) with Armijo backtracking along the projection arc. H is updated only when sᵀy > 0 (curvature condition) and is reset when μ changes. The problem is non-convex (bilinear control), so the method finds **local** solutions. Warm starts and continuation make the computed solutions reproducible, but global optimality is not claimed.

## 23. Reduced-order optimisation and certification (`ReducedOrder/ReducedOrderCoolingOptimizer.cs`)

The same adjoint optimiser runs on the POD model. Two additions make it safe:

1. **Constraint screening.** The ROM simulates the initial guess, and the state constraint is kept only on cells within δ of the instantaneous maximum at some time. The penalty normalisation still uses the full cell count, so μ keeps its meaning. This reduces the reconstruction cost from O(nr) to O(|C|r) per step.
2. **Certification and defect correction.** The ROM plan is evaluated with one full-order solve. If the full model violates T<sub>safe</sub>, the ROM constraint is tightened by the certified discrepancy, T<sub>safe</sub><sup>ROM</sup> ← T<sub>safe</sub><sup>ROM</sup> − (peak<sub>FOM</sub> − T<sub>safe</sub>), and the ROM problem is re-solved from the current plan. A residual violation is removed by a uniform-shift bisection on the full model. If the ROM's peak error exceeds the validation threshold, the ROM is **rejected** and the full-order adjoint optimiser runs instead. The returned plan is always feasible *on the high-fidelity model*.

## 24. Model-predictive control (`Application/Twin/DigitalTwinEngine.cs`)

Every 60 s the twin re-estimates (T̂, q̂), solves the optimal-control problem on a 6 × 100 s horizon from the current state, and applies the first segment (receding horizon). The shifted plan warm-starts the next solve. A 1 K back-off below T<sub>safe</sub> absorbs the twin's known under-estimation of the defect peak. The optimiser is configurable: the original max-penalty/finite-difference method, the full-order adjoint method (default), or the certified POD-ROM adjoint method.

## 25. Avoiding the inverse crime (`Simulation/BatteryPlant.cs`)

Synthetic data generated by the discretisation used for inversion make reconstructions look unrealistically good. The ground-truth plant therefore runs on a 2× finer grid (80×40), uses the exact Gaussian defect (not representable in the hat basis), and adds sensor noise. Fields are compared on the model grid after finite-volume restriction (cell averaging). All results are **synthetic benchmarks**. They are not validation against a physical battery.

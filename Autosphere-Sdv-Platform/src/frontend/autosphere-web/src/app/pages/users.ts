import { ChangeDetectionStrategy, Component, OnInit, inject, signal } from '@angular/core';
import { FormsModule } from '@angular/forms';
import { ApiService, errorMessage } from '../core/api.service';
import { Role, UserAccount } from '../core/models';

@Component({
  selector: 'app-users',
  imports: [FormsModule],
  changeDetection: ChangeDetectionStrategy.OnPush,
  template: `
    <div class="page">
      <div class="page-header">
        <div>
          <h1>Users</h1>
          <p>Administrators manage accounts and roles. Passwords are hashed by ASP.NET Core Identity and never displayed.</p>
        </div>
      </div>
      @if (message(); as m) { <div class="notice" [class.error]="m.error" role="status">{{ m.text }}</div> }
      <section class="panel table-wrap">
        <table>
          <thead><tr><th>User</th><th>E-mail</th><th>Role</th><th>Status</th><th></th></tr></thead>
          <tbody>
            @for (u of users(); track u.id) {
              <tr>
                <td><strong>{{ u.userName }}</strong></td>
                <td>{{ u.email }}</td>
                <td>
                  <select [value]="u.roles[0]" (change)="changeRole(u, $any($event.target).value)" [attr.aria-label]="'Role of ' + u.userName">
                    @for (r of roles; track r) { <option [value]="r">{{ r }}</option> }
                  </select>
                </td>
                <td>{{ u.isLockedOut ? 'locked out' : 'active' }}</td>
                <td><button class="small danger" (click)="remove(u)">Delete</button></td>
              </tr>
            }
          </tbody>
        </table>
      </section>
      <section class="panel">
        <h2>Create user</h2>
        <form class="form-row" (ngSubmit)="create()" style="margin-top: 10px">
          <label>User name<input name="u" [(ngModel)]="draft.userName" required /></label>
          <label>E-mail<input name="e" type="email" [(ngModel)]="draft.email" required /></label>
          <label>Password<input name="p" type="password" [(ngModel)]="draft.password" required minlength="12" autocomplete="new-password" /></label>
          <label>Role
            <select name="r" [(ngModel)]="draft.role">@for (r of roles; track r) { <option [value]="r">{{ r }}</option> }</select>
          </label>
          <button class="primary" type="submit">Create</button>
        </form>
        <p class="muted">Passwords need at least 12 characters with upper and lower case, a digit and a symbol.</p>
      </section>
    </div>
  `,
})
export class UsersPage implements OnInit {
  private readonly api = inject(ApiService);
  protected readonly roles: Role[] = ['Administrator', 'Engineer', 'Viewer'];
  protected readonly users = signal<UserAccount[]>([]);
  protected readonly message = signal<{ text: string; error: boolean } | null>(null);
  protected draft = { userName: '', email: '', password: '', role: 'Viewer' as Role };

  async ngOnInit(): Promise<void> {
    await this.load();
  }

  protected async create(): Promise<void> {
    await this.run(async () => {
      await this.api.createUser(this.draft);
      const name = this.draft.userName;
      this.draft = { userName: '', email: '', password: '', role: 'Viewer' };
      return `User ${name} created.`;
    });
  }

  protected changeRole(user: UserAccount, role: Role): Promise<void> {
    return this.run(async () => {
      await this.api.changeRole(user.id, role);
      return `${user.userName} is now ${role}.`;
    });
  }

  protected remove(user: UserAccount): Promise<void> {
    if (!confirm(`Delete user ${user.userName}?`)) {
      return Promise.resolve();
    }
    return this.run(async () => {
      await this.api.deleteUser(user.id);
      return `${user.userName} deleted.`;
    });
  }

  private async load(): Promise<void> {
    this.users.set(await this.api.users());
  }

  private async run(action: () => Promise<string>): Promise<void> {
    try {
      this.message.set({ text: await action(), error: false });
    } catch (error) {
      this.message.set({ text: errorMessage(error), error: true });
    }
    await this.load();
  }
}

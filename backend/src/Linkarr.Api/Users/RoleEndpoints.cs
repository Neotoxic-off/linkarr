using Linkarr.Api.Auth;
using Linkarr.Api.Data;
using Linkarr.Api.Sync;
using Microsoft.EntityFrameworkCore;

namespace Linkarr.Api.Users;

// Admin only: roles (what a user may do) and groups (which libraries it sees). Every change re-provisions the users it touches,
// through the user sync a few seconds later (a run of edits makes one), each user showing Pending until then.
public static class RoleEndpoints
{
    public sealed record RoleRequest(string Name, Rights Rights);

    public sealed record GroupRequest(string Name, bool AllLibraries, IReadOnlyList<string>? Libraries, int? DefaultRoleId, IReadOnlyList<int>? Members);

    public const int MaxNameLength = 48;

    public static RouteGroupBuilder MapRoleApi(this RouteGroupBuilder api)
    {
        RouteGroupBuilder roles = api.MapGroup("/roles").RequireAuthorization(AuthEndpoints.AdminPolicy);
        roles.MapGet("/", async (LinkarrDbContext db, CancellationToken ct) =>
        {
            ILookup<int, int> members = (await db.Accounts.AsNoTracking().Select(a => new { a.Id, a.RoleId }).ToListAsync(ct)).ToLookup(a => a.RoleId, a => a.Id);
            return (await db.Roles.AsNoTracking().OrderBy(r => r.Id).ToListAsync(ct)).Select(r => RoleDto(r, members[r.Id]));
        });

        roles.MapPost("/", async (RoleRequest request, LinkarrDbContext db, CancellationToken ct) =>
        {
            Role role = new() { Name = await ValidNameAsync(request.Name, db.Roles, null, ct), Rights = ValidRights(request.Rights) };
            db.Roles.Add(role);
            await db.SaveChangesAsync(ct);
            return RoleDto(role, []);
        });

        roles.MapPut("/{id:int}", async (int id, RoleRequest request, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            Role role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException($"Role {id} does not exist.");
            if (role.Id == Role.AdminId)
            {
                throw new ArgumentException("The Admin role always has everything; it cannot be changed.", nameof(id));
            }

            Rights rights = ValidRights(request.Rights);
            if (role.Admin && !rights.Admin && !await db.Accounts.AnyAsync(a => a.Role.Admin && !a.Disabled && a.RoleId != id, ct))
            {
                throw new ArgumentException("This role holds the last enabled admins: it cannot lose the admin right.", nameof(request));
            }

            if (!role.Admin && rights.Admin && await db.Groups.AnyAsync(g => g.DefaultRoleId == id, ct))
            {
                throw new ArgumentException("A group gives this role to its new users: a group's default role cannot be an admin role.", nameof(request));
            }

            role.Name = await ValidNameAsync(request.Name, db.Roles, id, ct);
            role.Rights = rights;
            await db.SaveChangesAsync(ct);
            List<int> members = await db.Accounts.Where(a => a.RoleId == id).Select(a => a.Id).ToListAsync(ct);
            await ResyncAsync(db, state, members, ct);
            return RoleDto(role, members);
        });

        // Only an unused custom role: nobody is ever left without one.
        roles.MapDelete("/{id:int}", async (int id, LinkarrDbContext db, CancellationToken ct) =>
        {
            Role role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct) ?? throw new KeyNotFoundException($"Role {id} does not exist.");
            if (role.BuiltIn is not null)
            {
                throw new ArgumentException($"{role.Name} is a built-in role; it cannot be deleted.", nameof(id));
            }

            if (await db.Accounts.AnyAsync(a => a.RoleId == id, ct))
            {
                throw new ArgumentException($"{role.Name} still has users: give them another role first.", nameof(id));
            }

            db.Roles.Remove(role);
            await db.SaveChangesAsync(ct);
            return Results.NoContent();
        });

        RouteGroupBuilder groups = api.MapGroup("/groups").RequireAuthorization(AuthEndpoints.AdminPolicy);
        groups.MapGet("/", async (LinkarrDbContext db, CancellationToken ct) =>
            (await db.Groups.AsNoTracking().Include(g => g.Members).OrderBy(g => g.Name).ToListAsync(ct)).Select(GroupDto));

        groups.MapPost("/", async (GroupRequest request, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            Group group = new() { Name = "" };
            await ApplyAsync(group, request, db, ct);
            db.Groups.Add(group);
            await db.SaveChangesAsync(ct);
            await ResyncAsync(db, state, group.Members.Select(m => m.Id), ct);
            return GroupDto(group);
        });

        groups.MapPut("/{id:int}", async (int id, GroupRequest request, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            Group group = await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new KeyNotFoundException($"Group {id} does not exist.");
            List<int> before = [.. group.Members.Select(m => m.Id)];
            await ApplyAsync(group, request, db, ct);
            await db.SaveChangesAsync(ct);
            await ResyncAsync(db, state, before.Concat(group.Members.Select(m => m.Id)), ct);
            return GroupDto(group);
        });

        groups.MapDelete("/{id:int}", async (int id, LinkarrDbContext db, SyncState state, CancellationToken ct) =>
        {
            Group group = await db.Groups.Include(g => g.Members).FirstOrDefaultAsync(g => g.Id == id, ct) ?? throw new KeyNotFoundException($"Group {id} does not exist.");
            List<int> members = [.. group.Members.Select(m => m.Id)];
            db.Groups.Remove(group);
            await db.SaveChangesAsync(ct);
            await ResyncAsync(db, state, members, ct);
            return Results.NoContent();
        });
        return api;
    }

    private static object RoleDto(Role role, IEnumerable<int> members) => new { role.Id, role.Name, role.BuiltIn, rights = role.Rights, members };

    private static object GroupDto(Group group) => new
    {
        group.Id, group.Name, group.AllLibraries, group.Libraries, group.DefaultRoleId,
        members = group.Members.Select(m => m.Id).Order(),
    };

    // Members replace the group's; a group gives libraries only, so who is in it changes nobody's role.
    private static async Task ApplyAsync(Group group, GroupRequest request, LinkarrDbContext db, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        group.Name = await ValidNameAsync(request.Name, db.Groups, group.Id == 0 ? null : group.Id, ct);
        if (request.DefaultRoleId is int roleId)
        {
            Role role = await db.Roles.AsNoTracking().FirstOrDefaultAsync(r => r.Id == roleId, ct) ?? throw new ArgumentException($"Role {roleId} does not exist.", nameof(request));
            if (role.Admin)
            {
                throw new ArgumentException("A group's default role cannot be an admin role.", nameof(request));
            }
        }

        List<int> ids = [.. (request.Members ?? []).Distinct()];
        List<Account> members = ids.Count == 0 ? [] : await db.Accounts.Where(a => ids.Contains(a.Id)).ToListAsync(ct);
        if (members.Count != ids.Count)
        {
            throw new ArgumentException("A member does not exist.", nameof(request));
        }

        group.AllLibraries = request.AllLibraries;
        group.Libraries = request.AllLibraries ? [] : Role.CleanLibraries(request.Libraries);
        group.DefaultRoleId = request.DefaultRoleId;
        group.Members.Clear();
        group.Members.AddRange(members);
    }

    private static async Task<string> ValidNameAsync<T>(string? name, DbSet<T> set, int? self, CancellationToken ct) where T : class
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            throw new ArgumentException($"The name must be 1 to {MaxNameLength} characters.", nameof(name));
        }

        // Both tables compare names without case (NOCASE), like usernames.
        if (await set.AnyAsync(e => EF.Property<string>(e, "Name") == trimmed && EF.Property<int>(e, "Id") != (self ?? 0), ct))
        {
            throw new ArgumentException($"'{trimmed}' is already taken.", nameof(name));
        }

        return trimmed;
    }

    private static Rights ValidRights(Rights? rights)
    {
        ArgumentNullException.ThrowIfNull(rights);
        if (rights.RequestsPerDay is < 0 or > 1000 || rights.MaxParentalRating is < 0 or > 1000 || rights.MaxActiveSessions is < 0 or > 100 || rights.BitrateLimit < 0)
        {
            throw new ArgumentException("A limit is out of range.", nameof(rights));
        }

        return rights;
    }

    // Marked Pending now; the worker's reconcile provisions them once the edits stop for SyncState.RightsDebounce.
    private static async Task ResyncAsync(LinkarrDbContext db, SyncState state, IEnumerable<int> accountIds, CancellationToken ct)
    {
        List<int> ids = [.. accountIds.Distinct()];
        if (ids.Count == 0)
        {
            return;
        }

        await db.Accounts.Where(a => ids.Contains(a.Id)).ExecuteUpdateAsync(s => s.SetProperty(a => a.SyncStatus, SyncStatus.Pending), ct);
        state.RequestUsers(SyncState.RightsDebounce);
    }
}

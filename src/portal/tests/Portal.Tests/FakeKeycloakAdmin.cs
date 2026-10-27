using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;

namespace Portal.Tests;

/// <summary>
/// In-memory фейк Keycloak Admin REST API + token endpoint (client_credentials):
/// закрывает все маршруты, которые использует KeycloakAdminClient портала.
/// Seeded-данные — эталонные значения контракта jwt-claims.md §4 (task-02).
/// Позволяет тестировать орг-изоляцию/валидации портала без живого Keycloak
/// (docker-сокет в среде агента недоступен — как в task-01…06).
/// </summary>
public sealed class FakeKeycloakAdmin
{
    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);

    public sealed record FakeUser(
        string Id, string Username, string? FirstName, string? LastName,
        string OrgId, HashSet<string> Roles, HashSet<string> GroupIds,
        string ServicesAttr)
    {
        public bool Enabled = true;
        public bool PasswordTemporary;
        public bool PasswordSet;
    }

    public sealed record FakeOrg(string Id, string Alias, string Name, HashSet<string> Services);

    public sealed record FakeGroup(string Id, string Name);

    public ConcurrentDictionary<string, FakeUser> Users { get; } = new();
    public ConcurrentDictionary<string, FakeOrg> Orgs { get; } = new();
    public ConcurrentDictionary<string, FakeGroup> Groups { get; } = new();

    /// <summary>Лог выданных client_credentials-токенов (для проверок сервис-аккаунта).</summary>
    public ConcurrentQueue<string> ServiceTokensIssued { get; } = new();

    public FakeUser User(string username) => Users.Values.First(u => u.Username == username);

    public FakeKeycloakAdmin()
    {
        // роли (id фиксированы — DELETE role-mappings требует id+name)
        foreach (var (id, name) in new[]
                 {
                     ("role-user-id", "user"), ("role-admin-id", "admin"), ("role-auditor-id", "auditor"),
                 })
            _roles[name] = new KcRoleJson(id, name);

        var orga = new FakeOrg("org-orga-id", "orga", "Org A",
            ["cryptoservice", "cadastralservice", "cadastralanalyticservice"]);
        var orgb = new FakeOrg("org-orgb-id", "orgb", "Org B",
            ["cryptoservice", "cadastralservice", "cadastralanalyticservice"]);
        Orgs[orga.Id] = orga;
        Orgs[orgb.Id] = orgb;

        foreach (var name in new[] { "orga-managers", "orga-team-a", "orgb-managers", "orgb-team-b" })
        {
            var g = new FakeGroup($"group-{name}-id", name);
            Groups[g.Id] = g;
        }

        void Seed(string username, string first, string last, FakeOrg org, string[] roles,
            string[] groups, string services)
        {
            var u = new FakeUser($"user-{username}-id", username, first, last, org.Id,
                roles.ToHashSet(), groups.Select(gn => $"group-{gn}-id").ToHashSet(), services)
            {
                PasswordSet = true,
            };
            Users[u.Id] = u;
        }

        Seed("alice@orga.example.com", "Alice", "Admin", orga, ["user", "admin"], ["orga-managers"],
            "cryptoservice cadastralservice cadastralanalyticservice");
        Seed("bob@orga.example.com", "Bob", "Backoff", orga, ["user"], ["orga-team-a"],
            "cryptoservice cadastralservice");
        Seed("carol@orga.example.com", "Carol", "Carlson", orga, ["user", "auditor"], ["orga-team-a"],
            "cadastralanalyticservice");
        Seed("dave@orgb.example.com", "Dave", "Davidson", orgb, ["user", "admin"], ["orgb-managers"],
            "cryptoservice cadastralservice cadastralanalyticservice");
        Seed("erin@orgb.example.com", "Erin", "Erson", orgb, ["user"], ["orgb-team-b"],
            "cryptoservice");
    }

    private readonly Dictionary<string, KcRoleJson> _roles = new();

    public sealed record KcRoleJson(string Id, string Name);

    // ── HTTP-часть ──────────────────────────────────────────────────────────────

    public HttpMessageHandler CreateHandler() => new Handler(this);

    private sealed class Handler(FakeKeycloakAdmin s) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage req,
            CancellationToken ct)
        {
            var path = req.RequestUri!.AbsolutePath;
            var method = req.Method.Method;
            var query = req.RequestUri.Query;

            // ── token endpoint (client_credentials для сервис-аккаунта portal) ──
            if (path == "/realms/rulink/protocol/openid-connect/token" && method == "POST")
            {
                var form = await req.Content!.ReadAsStringAsync(ct);
                if (!form.Contains("grant_type=client_credentials"))
                    return Json(HttpStatusCode.BadRequest, new { error = "unsupported_grant_type" });
                if (!form.Contains("client_id=portal") || !form.Contains("client_secret=test-secret"))
                    return Json(HttpStatusCode.Unauthorized, new { error = "invalid_client" });
                var token = $"svc-token-{Guid.NewGuid():N}";
                s.ServiceTokensIssued.Enqueue(token);
                return Json(HttpStatusCode.OK, new { access_token = token, expires_in = 300 });
            }

            // ── admin REST ──────────────────────────────────────────────────────
            const string admin = "/admin/realms/rulink";
            if (!path.StartsWith(admin)) return new HttpResponseMessage(HttpStatusCode.NotFound);
            var rest = path[admin.Length..].TrimStart('/');
            var parts = rest.Split('/');

            // organizations
            if (rest == "organizations" && method == "GET")
                return Json(HttpStatusCode.OK, s.Orgs.Values.Select(OrgJson));
            if (rest == "organizations" && method == "POST")
            {
                var body = await ReadJsonAsync(req, ct);
                var alias = body.RootElement.GetProperty("alias").GetString()!;
                if (s.Orgs.Values.Any(o => o.Alias == alias))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                var org = new FakeOrg($"org-{alias}-{Guid.NewGuid():N}"[..40], alias,
                    body.RootElement.GetProperty("name").GetString()!,
                    AttrServices(body).ToHashSet());
                s.Orgs[org.Id] = org;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Location = new Uri($"{admin}/organizations/{org.Id}", UriKind.Relative) },
                };
            }
            if (parts.Length >= 2 && parts[0] == "organizations")
            {
                var orgId = parts[1];
                if (!s.Orgs.TryGetValue(orgId, out var org))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                if (parts.Length == 2 && method == "PUT")
                {
                    var body = await ReadJsonAsync(req, ct);
                    var name = body.RootElement.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var services = AttrServices(body);
                    var updated = org with { Name = name ?? org.Name, Services = services.ToHashSet() };
                    s.Orgs[org.Id] = updated;
                    return NoContent();
                }
                if (parts.Length == 3 && parts[2] == "members" && method == "GET")
                {
                    var first = Q(query, "first", 0);
                    var max = Q(query, "max", 100);
                    var search = Q(query, "search", "");
                    var members = s.Users.Values
                        .Where(u => u.OrgId == org.Id)
                        .Where(u => string.IsNullOrEmpty(search)
                                    || u.Username.Contains(search, StringComparison.OrdinalIgnoreCase))
                        .OrderBy(u => u.Username)
                        .Skip(first).Take(max)
                        .Select(UserJson);
                    return Json(HttpStatusCode.OK, members);
                }
                if (parts.Length == 3 && parts[2] == "members" && method == "POST")
                {
                    var body = await ReadJsonAsync(req, ct);
                    var userId = body.RootElement.GetString()!;
                    if (!s.Users.ContainsKey(userId))
                        return new HttpResponseMessage(HttpStatusCode.NotFound);
                    s.Users[userId] = s.Users[userId] with { OrgId = org.Id };
                    return NoContent();
                }
                if (parts.Length == 4 && parts[2] == "members" && method == "GET")
                {
                    var userId = parts[3];
                    return s.Users.TryGetValue(userId, out var u) && u.OrgId == org.Id
                        ? Json(HttpStatusCode.OK, UserJson(u))
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                }
            }

            // groups
            if (rest == "groups" && method == "GET")
                return Json(HttpStatusCode.OK, s.Groups.Values.Select(g => new { g.Id, g.Name }));
            if (rest == "groups" && method == "POST")
            {
                var body = await ReadJsonAsync(req, ct);
                var name = body.RootElement.GetProperty("name").GetString()!;
                if (s.Groups.Values.Any(g => g.Name == name))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                var g = new FakeGroup($"group-{name}-{Guid.NewGuid():N}"[..40], name);
                s.Groups[g.Id] = g;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Location = new Uri($"{admin}/groups/{g.Id}", UriKind.Relative) },
                };
            }
            if (parts.Length == 2 && parts[0] == "groups")
            {
                var gid = parts[1];
                if (method == "GET")
                    return s.Groups.TryGetValue(gid, out var g)
                        ? Json(HttpStatusCode.OK, new { g.Id, g.Name })
                        : new HttpResponseMessage(HttpStatusCode.NotFound);
                if (method == "DELETE")
                {
                    s.Groups.TryRemove(gid, out _);
                    foreach (var u in s.Users.Values.Where(u => u.GroupIds.Contains(gid)).ToList())
                        u.GroupIds.Remove(gid);
                    return NoContent();
                }
            }

            // users
            if (rest == "users" && method == "GET")
            {
                var username = Q(query, "username", "");
                var exact = Q(query, "exact", "") == "true";
                var found = s.Users.Values
                    .Where(u => exact ? u.Username == username : username == "" || u.Username.Contains(username))
                    .Select(UserJson);
                return Json(HttpStatusCode.OK, found);
            }
            if (rest == "users" && method == "POST")
            {
                var body = await ReadJsonAsync(req, ct);
                var root = body.RootElement;
                var username = root.GetProperty("username").GetString()!;
                if (s.Users.Values.Any(u => u.Username == username))
                    return new HttpResponseMessage(HttpStatusCode.Conflict);
                var user = new FakeUser(
                    $"user-{Guid.NewGuid():N}", username,
                    TryStr(root, "firstName"), TryStr(root, "lastName"),
                    OrgId: "", Roles: [], GroupIds: [],
                    ServicesAttr: AttrUserServices(root))
                {
                    Enabled = TryBool(root, "enabled", true),
                };
                s.Users[user.Id] = user;
                return new HttpResponseMessage(HttpStatusCode.Created)
                {
                    Headers = { Location = new Uri($"{admin}/users/{user.Id}", UriKind.Relative) },
                };
            }
            if (parts.Length >= 2 && parts[0] == "users")
            {
                var uid = parts[1];
                if (!s.Users.TryGetValue(uid, out var user))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);

                if (parts.Length == 2 && method == "GET")
                    return Json(HttpStatusCode.OK, UserJson(user));
                if (parts.Length == 2 && method == "PUT")
                {
                    var body = await ReadJsonAsync(req, ct);
                    var root = body.RootElement;
                    var updated = user with
                    {
                        FirstName = TryStr(root, "firstName") ?? user.FirstName,
                        LastName = TryStr(root, "lastName") ?? user.LastName,
                        ServicesAttr = root.TryGetProperty("attributes", out _)
                            ? AttrUserServices(root)
                            : user.ServicesAttr,
                    };
                    updated.Enabled = TryBool(root, "enabled", user.Enabled);
                    s.Users[uid] = updated;
                    return NoContent();
                }
                if (parts.Length == 2 && method == "DELETE")
                {
                    s.Users.TryRemove(uid, out _);
                    return NoContent();
                }
                if (parts.Length == 3 && parts[2] == "reset-password" && method == "PUT")
                {
                    var body = await ReadJsonAsync(req, ct);
                    user.PasswordSet = true;
                    user.PasswordTemporary = body.RootElement.GetProperty("temporary").GetBoolean();
                    return NoContent();
                }
                if (parts.Length == 4 && parts[2] == "role-mappings" && parts[3] == "realm")
                {
                    if (method == "GET")
                        return Json(HttpStatusCode.OK,
                            user.Roles.Where(r => s._roles.ContainsKey(r))
                                .Select(r => new { s._roles[r].Id, Name = r }));
                    var body = await ReadJsonAsync(req, ct);
                    var roles = body.RootElement.EnumerateArray()
                        .Select(e => e.GetProperty("name").GetString()!).ToHashSet();
                    if (method == "POST")
                    {
                        foreach (var r in roles) user.Roles.Add(r);
                        return NoContent();
                    }
                    if (method == "DELETE")
                    {
                        foreach (var r in roles) user.Roles.Remove(r);
                        return NoContent();
                    }
                }
                if (parts.Length == 3 && parts[2] == "groups" && method == "GET")
                    return Json(HttpStatusCode.OK, user.GroupIds
                        .Where(s.Groups.ContainsKey).Select(g => new { s.Groups[g].Id, s.Groups[g].Name }));
                if (parts.Length == 4 && parts[2] == "groups" && method == "PUT")
                {
                    user.GroupIds.Add(parts[3]);
                    return NoContent();
                }
                if (parts.Length == 4 && parts[2] == "groups" && method == "DELETE")
                {
                    user.GroupIds.Remove(parts[3]);
                    return NoContent();
                }
            }

            // roles/{name}
            if (parts.Length == 2 && parts[0] == "roles" && method == "GET")
            {
                var name = Uri.UnescapeDataString(parts[1]);
                if (!s._roles.TryGetValue(name, out var role))
                    return new HttpResponseMessage(HttpStatusCode.NotFound);
                return Json(HttpStatusCode.OK, new { role.Id, role.Name });
            }

            return new HttpResponseMessage(HttpStatusCode.NotFound);
        }

        // ── хелперы ─────────────────────────────────────────────────────────────

        private static HttpResponseMessage Json(HttpStatusCode status, object body)
        {
            var payload = JsonSerializer.Serialize(body, JsonOpts);
            return new HttpResponseMessage(status)
            {
                Content = new StringContent(payload, Encoding.UTF8, new System.Net.Http.Headers.MediaTypeHeaderValue("application/json")),
            };
        }

        private static HttpResponseMessage NoContent() => new(HttpStatusCode.NoContent);

        private static async Task<JsonDocument> ReadJsonAsync(HttpRequestMessage req, CancellationToken ct)
            => await JsonDocument.ParseAsync(await req.Content!.ReadAsStreamAsync(ct), cancellationToken: ct);

        private static string? TryStr(JsonElement root, string prop)
            => root.TryGetProperty(prop, out var e) && e.ValueKind != JsonValueKind.Null
                ? e.GetString() : null;

        private static bool TryBool(JsonElement root, string prop, bool dflt)
            => root.TryGetProperty(prop, out var e)
               && (e.ValueKind is JsonValueKind.True or JsonValueKind.False)
                ? e.GetBoolean() : dflt;

        // attributes.availableServices — каждый сервис отдельным значением (task-02)
        private static string[] AttrServices(JsonDocument body)
        {
            if (!body.RootElement.TryGetProperty("attributes", out var attrs)) return [];
            if (!attrs.TryGetProperty("availableServices", out var list)) return [];
            return list.EnumerateArray().Select(e => e.GetString()!).ToArray();
        }

        // attributes.services — одна строка сервисов через пробел (task-02)
        private static string AttrUserServices(JsonElement root)
        {
            if (!root.TryGetProperty("attributes", out var attrs)) return "";
            if (!attrs.TryGetProperty("services", out var list)) return "";
            return string.Join(' ', list.EnumerateArray().Select(e => e.GetString() ?? ""));
        }

        private static object UserJson(FakeUser u) => new
        {
            u.Id,
            u.Username,
            Email = u.Username,
            u.FirstName,
            u.LastName,
            Enabled = u.Enabled,
            EmailVerified = true,
            Attributes = new Dictionary<string, string[]> { ["services"] = [u.ServicesAttr] },
        };

        private static object OrgJson(FakeOrg o) => new
        {
            o.Id,
            o.Alias,
            o.Name,
            Enabled = true,
            Attributes = new Dictionary<string, string[]> { ["availableServices"] = [.. o.Services] },
        };

        private static string Q(string query, string key, string dflt)
        {
            foreach (var pair in query.TrimStart('?').Split('&'))
            {
                var kv = pair.Split('=', 2);
                if (kv.Length == 2 && kv[0] == key)
                    return Uri.UnescapeDataString(kv[1]);
            }
            return dflt;
        }

        private static int Q(string query, string key, int dflt)
            => int.TryParse(Q(query, key, dflt.ToString()), out var v) ? v : dflt;
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text;
using MCHarbor.Core.Models;

namespace MCHarbor.Core.Services;

public sealed class AccountRepository(ISecretStore secrets)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string Index => Path.Combine(HarborHost.Paths.Config, "accounts.json");
    public bool PersistentSecrets => secrets.CanPersist;

    public IReadOnlyList<StoredAccount> ReadAll() => File.Exists(Index)
        ? JsonSerializer.Deserialize<StoredAccount[]>(File.ReadAllBytes(Index)) ?? [] : [];
    private static string Key(string id) => "account:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
    private static string OfflinePath(string id) => Path.Combine(HarborHost.Paths.Config, "offline-accounts", Key(id)[8..] + ".json");

    public async Task<StoredAccount> SaveAsync(JsonObject serialized, CancellationToken cancellation = default)
    {
        var metadata = serialized["metadata"] as JsonObject ?? throw new InvalidDataException("账号缺少身份信息。");
        var hmcl = serialized["hmclMetadata"] as JsonObject ?? throw new InvalidDataException("账号缺少 HMCL 信息。");
        var id = metadata["id"]?.GetValue<string>() ?? throw new InvalidDataException("账号缺少标识。");
        var kind = metadata["kind"]?.GetValue<string>() ?? "offline";
        if (id.Length is < 1 or > 256 || kind is not ("offline" or "microsoft" or "external" or "authlibInjector"))
            throw new InvalidDataException("账号标识或类型无效。");
        var payload = new JsonObject { ["hmclMetadata"] = hmcl.DeepClone(), ["privateData"] = serialized["privateData"]?.DeepClone() ?? new JsonObject() };
        await _gate.WaitAsync(cancellation);
        try
        {
            if (kind == "offline")
            {
                if (payload["privateData"] is JsonObject { Count: > 0 }) throw new InvalidDataException("离线账号不应包含登录凭据。");
                AtomicFile.Write(OfflinePath(id), Encoding.UTF8.GetBytes(payload.ToJsonString()));
            }
            else await secrets.WriteAsync(Key(id), payload.ToJsonString(), cancellation);
            var account = new StoredAccount(id, metadata["name"]?.GetValue<string>() ?? "Player",
                metadata["uuid"]?.GetValue<string>() ?? "", kind, serialized["requiresReauth"]?.GetValue<bool>() == true);
            AtomicFile.Write(Index, JsonSerializer.SerializeToUtf8Bytes(ReadAll().Where(row => row.Id != id).Append(account).ToArray()));
            return account;
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject?> LoadPayloadAsync(StoredAccount account, CancellationToken cancellation = default)
    {
        if (account.RequiresReauth) return null;
        var path = OfflinePath(account.Id);
        var text = account.Kind == "offline"
            ? File.Exists(path) ? await File.ReadAllTextAsync(path, cancellation) : null
            : await secrets.ReadAsync(Key(account.Id), cancellation);
        return text is null ? null : JsonNode.Parse(text)?.AsObject();
    }

    public async Task RemoveAsync(StoredAccount account, CancellationToken cancellation = default)
    {
        await _gate.WaitAsync(cancellation);
        try
        {
            if (account.Kind != "offline") await secrets.DeleteAsync(Key(account.Id), cancellation);
            else File.Delete(OfflinePath(account.Id));
            AtomicFile.Write(Index, JsonSerializer.SerializeToUtf8Bytes(ReadAll().Where(row => row.Id != account.Id).ToArray()));
            if (HarborHost.GetPreference("selectedAccount") == account.Id) HarborHost.SetPreference("selectedAccount", "");
        }
        finally { _gate.Release(); }
    }

    public async Task<JsonObject> ExportPayloadAsync(IEnumerable<StoredAccount> selected, CancellationToken cancellation = default)
    {
        var rows = new JsonArray();
        foreach (var account in selected.Where(account => account.Kind != "offline"))
        {
            var payload = await LoadPayloadAsync(account, cancellation) ?? throw new IOException("账号需要重新登录：" + account.Name);
            rows.Add(new JsonObject
            {
                ["metadata"] = new JsonObject { ["id"] = account.Id, ["name"] = account.Name, ["uuid"] = account.Uuid,
                    ["kind"] = account.Kind == "external" ? "authlibInjector" : account.Kind },
                ["hmclMetadata"] = payload["hmclMetadata"]!.DeepClone(), ["privateData"] = payload["privateData"]!.DeepClone()
            });
        }
        return new() { ["schema"] = "mc-harbor/account-migration", ["version"] = 1, ["accounts"] = rows };
    }
}

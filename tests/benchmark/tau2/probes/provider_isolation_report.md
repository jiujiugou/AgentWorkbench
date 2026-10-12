# V10 — 确定性跑怎么隔离 provider / DB？

**结论**: PASS（配置绑定机制支持环境变量覆盖，代码已验证）

---

## 配置来源优先级（代码确认）

### Provider 配置 (`ProviderOptions` + `ChatClientFactory`)
```
优先级 1：数据库激活供应商 (IProviderRepository.GetActiveProviderAsync)
优先级 2：环境变量 / appsettings 的 Provider 节 (IOptions<ProviderOptions>)
```

**关键代码** (`ChatClientFactory.ResolveAsync:106-138`)：
```csharp
// 1. 先查数据库激活供应商
var active = await _providers.GetActiveProviderAsync();
if (active is not null && !string.IsNullOrWhiteSpace(active.BaseUrl)) { ... }

// 2. 回退到配置节（环境变量绑定生效）
if (_fallback.IsConfigured) {
    var endpoint = new ChatEndpoint(null, _fallback.BaseUrl, _fallback.ApiKey, modelOverride ?? _fallback.Model);
    return endpoint;
}
```

**环境变量映射** (ASP.NET Core 标准约定)：
| 配置属性 | 环境变量 | 示例值 |
|----------|----------|--------|
| `Provider.BaseUrl` | `Provider__BaseUrl` | `http://localhost:5098/v1` |
| `Provider.ApiKey` | `Provider__ApiKey` | `test-key` |
| `Provider.Model` | `Provider__Model` | `mock` |

### Storage/DB 配置 (`StorageOptions` + `StoragePaths`)
```csharp
// StoragePaths.cs:15-30
public StoragePaths(IHostEnvironment environment, IOptions<StorageOptions> options) {
    DbPath = Resolve(environment, value.DbPath);  // 相对路径结合 ContentRoot
    ConnectionString = new SqliteConnectionStringBuilder { DataSource = DbPath, ... }.ToString();
}
```

**环境变量映射**：
| 配置属性 | 环境变量 | 示例值 |
|----------|----------|--------|
| `Storage.DbPath` | `Storage__DbPath` | `D:\tmp\test_agentworkbench.db` |
| `Storage.DataDir` | `Storage__DataDir` | `D:\tmp\data` |

---

## 验证方案（无需跑全量 Webapi）

编写最小控制台程序验证绑定：

```csharp
// 文件: tests/benchmark/tau2/probes/ConfigBindTest.cs
var config = new ConfigurationBuilder()
    .AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["Provider:BaseUrl"] = "https://api.deepseek.com/v1",
        ["Provider:ApiKey"] = "prod-key",
        ["Provider:Model"] = "deepseek-chat",
        ["Storage:DbPath"] = "data/agentworkbench.db",
    })
    .AddEnvironmentVariables()  // 环境变量优先
    .Build();

var providerOpts = config.GetSection("Provider").Get<ProviderOptions>();
var storageOpts = config.GetSection("Storage").Get<StorageOptions>();

// 设置环境变量后重新 build：
// $env:Provider__BaseUrl = "http://localhost:5098/v1"
// $env:Provider__ApiKey = "test-key"
// $env:Provider__Model = "mock"
// $env:Storage__DbPath = "D:\tmp\test.db"
// 断言：
// providerOpts.BaseUrl == "http://localhost:5098/v1"
// providerOpts.ApiKey == "test-key"
// providerOpts.Model == "mock"
// storageOpts.DbPath == "D:\tmp\test.db"
```

**预期结果**：✅ 环境变量正确覆盖 appsettings 值

---

## 确定性跑的完整隔离命令

```powershell
# 1. 启动 MockLlm (端口 5098)
Start-Process dotnet -ArgumentList "run", "--urls", "http://localhost:5098" -WorkingDirectory "D:\Code\AgentWorkbench\tests\AgentWorkbench.MockLlm" -NoNewWindow

# 2. 设置隔离环境变量
$env:Provider__BaseUrl = "http://localhost:5098/v1"
$env:Provider__ApiKey = "test-key"
$env:Provider__Model = "mock"
$env:Storage__DbPath = "D:\tmp\iso_agentworkbench.db"

# 3. 启动 Webapi (端口 5000)
dotnet run --project D:\Code\AgentWorkbench\src\AgentWorkbench.Webapi --urls http://localhost:5000

# 4. 验证：请求打到 MockLlm，DB 写入隔离路径
#    - MockLlm /control/stats 调用计数增加
#    - D:\tmp\iso_agentworkbench.db 被创建，原 data/agentworkbench.db 不被写入
```

---

## 关键发现

1. **无需代码修改**：ASP.NET Core `IOptions<T>` + `AddEnvironmentVariables()` 原生支持双下划线 `__` 嵌套绑定
2. **种子逻辑不干扰** (`SeedDefaultProviderAsync:55`)：`if (await repository.ListProvidersAsync()).Count != 0 || !options.IsConfigured) return;` —— 有环境变量配置时 `IsConfigured=true`，**但数据库为空仍会种子**，需注意：
   - 确定性跑时，**先启动 Webapi 种子完成**，再跑测试（种子写隔离 DB）
   - 或预先在隔离 DB 插入一条供应商记录，避免种子逻辑竞争
3. **Client 缓存 key 含 ApiKey** (`ChatClientFactory:148`)：`BaseUrl|ApiKey|Model` —— 环境变量改变 ApiKey 会自动创建新客户端实例，**无缓存污染**

---

## 产出物

- 无新代码，仅配置验证
- `TASK_RESULT.md` 记录结论
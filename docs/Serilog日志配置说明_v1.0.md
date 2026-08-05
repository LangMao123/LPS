# Serilog 日志配置说明

**版本**: v1.0  
**日期**: 2025-01-XX  
**作者**: 开发团队

---

## 1. 概述

为了更好地排查 Hangfire 后台任务执行问题，项目已集成 Serilog 结构化日志框架，支持按日志级别分文件夹存储。

### 1.1 核心特性

- **分级存储**: 按 Info/Warn/Error 级别分文件夹存储
- **滚动策略**: 按天滚动，自动归档历史日志
- **保留策略**: Info/Warn 保留 30 天，Error 保留 90 天
- **文件大小限制**: 单文件最大 100MB，超出自动分割
- **结构化日志**: 支持结构化查询和分析

---

## 2. 日志文件结构

```
logs/
├── all/              # 所有级别日志（完整记录）
│   └── aps-20250115.log
├── info/             # Information 级别
│   └── aps-info-20250115.log
├── warn/             # Warning 级别
│   └── aps-warn-20250115.log
└── error/            # Error 级别
    └── aps-error-20250115.log
```

### 2.1 文件命名规则

- **all**: `aps-{Date}.log`
- **info**: `aps-info-{Date}.log`
- **warn**: `aps-warn-{Date}.log`
- **error**: `aps-error-{Date}.log`

### 2.2 保留策略

| 文件夹 | 保留天数 | 说明 |
|--------|----------|------|
| all    | 30 天    | 完整日志，用于全面分析 |
| info   | 30 天    | 常规信息日志 |
| warn   | 30 天    | 警告日志 |
| error  | 90 天    | 错误日志，保留更长时间便于追溯 |

---

## 3. 日志级别配置

### 3.1 生产环境 (appsettings.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Information",
      "Override": {
        "LPS.APS": "Debug",
        "LPS.APS.Engine": "Debug",
        "LPS.APS.Engine.Services.Sync": "Debug",
        "Microsoft.AspNetCore": "Warning",
        "Microsoft.EntityFrameworkCore": "Warning",
        "Hangfire": "Information"
      }
    }
  }
}
```

**关键配置**:
- 默认级别: `Information`
- 业务代码: `Debug` (LPS.APS.*)
- 框架代码: `Warning` (Microsoft.*)
- Hangfire: `Information` (记录任务执行信息)

### 3.2 开发环境 (appsettings.Development.json)

```json
{
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "LPS.APS": "Debug",
        "LPS.APS.Engine.Services.Sync": "Debug"
      }
    }
  }
}
```

**开发环境特点**:
- 更详细的日志输出
- 同步服务 Debug 级别，便于排查问题

---

## 4. 日志输出格式

### 4.1 控制台输出

```
[2025-01-15 14:30:25.123 INF] LPS.APS.Engine.Services.Sync.ERPOrderSyncService
    开始全量同步 ERP 订单...
```

### 4.2 文件输出

**all/info/warn**:
```
[2025-01-15 14:30:25.123 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 开始全量同步 ERP 订单...
```

**error**:
```
[2025-01-15 14:30:25.123 ERROR] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 同步失败
System.Exception: 数据库连接超时
   at LPS.APS.Engine.Services.Sync.ERPOrderSyncService.FullSyncAsync()
----------------------------------------
```

---

## 5. 使用示例

### 5.1 在代码中记录日志

```csharp
public class ERPOrderSyncService
{
    private readonly ILogger<ERPOrderSyncService> _logger;

    public ERPOrderSyncService(ILogger<ERPOrderSyncService> logger)
    {
        _logger = logger;
    }

    public async Task FullSyncAsync()
    {
        _logger.LogInformation("开始全量同步 ERP 订单...");
        
        try
        {
            var orders = await QueryOrdersFromODSAsync();
            _logger.LogInformation("从 ODS 查询到 {Count} 条订单", orders.Count);
            
            // 业务逻辑...
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "全量同步失败");
            throw;
        }
    }
}
```

### 5.2 结构化日志

```csharp
_logger.LogInformation(
    "订单同步完成: 成功={SuccessCount}, 失败={FailCount}, 耗时={Duration}ms",
    successCount,
    failCount,
    stopwatch.ElapsedMilliseconds
);
```

---

## 6. 排查 Hangfire 任务问题

### 6.1 查看任务执行日志

1. **查看完整日志**: `logs/all/aps-{Date}.log`
2. **查看错误日志**: `logs/error/aps-error-{Date}.log`
3. **搜索关键字**: 使用文本编辑器或 `grep` 搜索任务名称

### 6.2 常见问题排查

**问题**: Hangfire 显示任务成功，但没有数据

**排查步骤**:
1. 打开 `logs/all/aps-{Date}.log`
2. 搜索任务开始时间（如 `00:30`）
3. 查找 `ERPOrderSyncService` 相关日志
4. 检查是否有 Warning 或 Error 级别日志

**示例日志**:
```
[2025-01-15 00:30:00.123 INF] [LPS.APS.Engine.Jobs.NightlyBatchOrchestrator] 开始执行夜间批处理任务
[2025-01-15 00:30:00.456 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 开始全量同步 ERP 订单...
[2025-01-15 00:30:05.789 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 从 ODS 查询到 1250 条订单
[2025-01-15 00:30:10.123 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 批量插入 1250 条订单到 Staging 表
[2025-01-15 00:30:15.456 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 调用存储过程验证并提升订单
[2025-01-15 00:30:20.789 INF] [LPS.APS.Engine.Services.Sync.ERPOrderSyncService] 全量同步完成: 成功=1200, 失败=50, 耗时=20333ms
```

---

## 7. 性能考虑

### 7.1 文件大小限制

- 单文件最大 100MB
- 超出后自动分割为 `aps-{Date}_001.log`, `aps-{Date}_002.log`

### 7.2 磁盘空间估算

假设每天生成 50MB 日志：
- **all**: 50MB × 30 天 = 1.5GB
- **info**: 30MB × 30 天 = 900MB
- **warn**: 10MB × 30 天 = 300MB
- **error**: 5MB × 90 天 = 450MB
- **总计**: 约 3.15GB

### 7.3 清理策略

Serilog 会自动删除超过保留期限的日志文件，无需手动清理。

---

## 8. 配置文件位置

- **生产配置**: `LPS.APS.Web/appsettings.json`
- **开发配置**: `LPS.APS.Web/appsettings.Development.json`
- **Program.cs**: `LPS.APS.Web/Program.cs` (第 10-11 行)

---

## 9. 依赖包

```xml
<PackageReference Include="Serilog.AspNetCore" Version="8.0.3" />
<PackageReference Include="Serilog.Sinks.Console" Version="6.0.0" />
<PackageReference Include="Serilog.Sinks.File" Version="6.0.0" />
<PackageReference Include="Serilog.Expressions" Version="5.0.0" />
```

---

## 10. 注意事项

1. **首次运行**: 日志文件夹会自动创建，无需手动创建
2. **权限要求**: 确保应用有 `logs/` 目录的写入权限
3. **日志级别**: 生产环境避免使用 `Trace` 级别，会产生大量日志
4. **敏感信息**: 避免记录密码、Token 等敏感信息
5. **异常处理**: 使用 `_logger.LogError(ex, "消息")` 记录完整异常堆栈

---

## 11. 后续优化建议

1. **集成 Seq**: 考虑使用 Seq 进行集中式日志管理和查询
2. **告警机制**: 配置 Error 级别日志的邮件或短信告警
3. **日志分析**: 定期分析 Error 日志，识别系统瓶颈
4. **性能监控**: 记录关键操作的耗时，用于性能优化

---

**文档结束**

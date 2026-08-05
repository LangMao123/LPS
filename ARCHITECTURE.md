# LPS.APS 技术架构决策说明

> 本文档供架构审查使用，说明项目结构的设计依据、收益和约束。

## 一、项目分层（7 个项目）

```
Web → Application → { Engine, BusinessRules, Scheduling } → Core → Shared
```

### 1.1 分层依据

| 项目 | 分层角色 | 设计模式 | 依据 |
|------|---------|---------|------|
| **Shared** | 基础设施层 | — | 存放跨层共享的模型（DTO）和配置选项，避免各层重复定义同一数据结构 |
| **Core** | 领域模型层 | DDD Entity/Value Object | 27个领域实体（APS 16个 + Auth 11个）集中管理，作为全系统的"单一事实来源" |
| **Engine** | 数据访问层 | Repository Pattern + Anti-Corruption Layer | 封装三库访问细节，对上层提供干净的仓储接口。防腐层隔离外部 ERP 数据源 |
| **Scheduling** | 算法层 | Strategy Pattern | 排程算法独立为一个项目，**项目级零数据库包依赖**，从编译层面强制保障"纯内存、零I/O" |
| **BusinessRules** | 规则层 | Plugin Pattern | 业务规则（Pegging、LotSizing、优先级）独立于算法层和数据层，可独立测试和替换 |
| **Application** | 应用服务层 | Use Case / Application Service | 编排调度 Engine + Scheduling + BusinessRules，实现具体用例，不含计算逻辑 |
| **Web** | 表现层 | MVC/API | HTTP 入口、中间件、DI 组装。不含任何业务逻辑 |

### 1.2 分层收益

- **编译级隔离**：Scheduling 项目不引用任何数据库包（Dapper、EF Core、SqlClient），如果有人在算法层写了数据库代码，编译直接报错
- **独立可测性**：每层可独立单元测试。算法层纯内存测试无需数据库；规则层 Mock 数据即可；仓储层可用内存数据库
- **团队并行开发**：5个号位各负责不同层，互不阻塞。只需遵守接口契约
- **替换灵活性**：更换排程算法只改 Scheduling 层；更换数据库只改 Engine 层；更换业务规则只改 BusinessRules 层

### 1.3 依赖原则

```
严格单向依赖，无循环引用：

Web ──→ Application ──→ Core
 │         │   │          ↑
 │         │   ├→ Engine ─┤
 │         │   ├→ BusinessRules ──→ Shared
 │         │   └→ Scheduling ─────→ Shared
 │         │                         ↑
 ├→ Engine ──→ Core ─────────────────┘
 └→ Shared
```

**依据**：Robert C. Martin《Clean Architecture》的依赖规则 —— 依赖方向必须从外圈指向内圈，内层（Core）不依赖外层（Engine/Web）。

---

## 二、三库物理隔离

### 2.1 架构

| 数据库 | 用途 | 访问技术 | CommandTimeout |
|--------|------|---------|----------------|
| **APS_Production** | 排程计算、主数据、订单、任务、库存 | Dapper + SqlBulkCopy | 60s |
| **MES_Integration (ODS)** | BOM展开、ERP契约视图（防腐层） | Dapper | 120s（BOM展开重） |
| **APS_Auth** | RBAC权限、审批流、审计日志 | EF Core | 30s（轻量查询） |

### 2.2 为什么三库而不是一库？

1. **性能隔离**：APS 排程计算是 CPU+IO 密集型操作，BOM 展开涉及递归存储过程。放在一个库会互相抢锁、抢 TempDB
2. **安全隔离**：权限数据（密码哈希、审计日志）与业务数据物理分离，满足安全审计要求
3. **职责隔离**：ODS 库是"防腐层"，由 SQL Server Agent Job 驱动 ETL，APS 只通过视图读取。即使 ERP 侧数据结构变更，只需修改 ODS 视图定义，APS 代码无需改动
4. **独立运维**：三库可分别备份、扩容、设置不同的恢复模型

### 2.3 为什么 APS/ODS 用 Dapper，Auth 用 EF Core？

| 选型 | 理由 |
|------|------|
| **Dapper（APS/ODS）** | 排程数据读写量大，需要精确控制 SQL、利用 SqlBulkCopy 批量写入、调用存储过程。ORM 的对象跟踪在此场景下是纯开销 |
| **EF Core（Auth）** | 权限模型关系复杂（User↔Role↔Permission 多对多 + 审批流），EF Core 的导航属性、变更跟踪、自动审计字段填充大幅减少代码量 |

**依据**：不同的数据访问模式使用不同的工具。这是 Pragmatic Architecture 的核心原则 —— 没有银弹，按场景选型。

---

## 三、防腐层设计（Anti-Corruption Layer）

### 3.1 问题

APS 系统需要读取 ERP 的销售订单数据，但：
- 不能直连 ERP 数据库（跨系统耦合）
- ERP 侧无法启用 CDC（Change Data Capture）
- ERP 数据结构可能随升级变化

### 3.2 解决方案：Socket-Plug 模式

```
ERP 数据库 → [SQL Server Agent Job ETL] → ODS 契约视图 (ext_v_APS_SalesOrder)
                                              ↓
                                     APS Engine 通过 Dapper 读取
                                              ↓
                                     ERP_Order_Staging 暂存表
                                              ↓
                                     sp_ValidateAndPromoteOrders 验证提升
                                              ↓
                                     Order_Canonical 标准订单表
```

- **契约视图** `ext_v_*` 是 ODS 库对外暴露的稳定接口，字段名和类型由 APS 团队定义
- **水位线增量同步**：基于 `UpdatedAt` 时间戳轮询（每小时），全量补偿（每日00:30）
- **好处**：ERP 随便改内部结构，只要 DBA 维护好视图映射，APS 代码零修改

**依据**：Eric Evans《Domain-Driven Design》第14章 Anti-Corruption Layer 模式。

---

## 四、仓储分层（Repository Organization）

### 4.1 结构

```
Engine/Repositories/
├── Base/           # BaseRepository（重试机制） + IRepository（基础接口）
├── APS/            # IJobRepository, IMachineRepository, IScheduleRepository（Dapper）
└── Auth/           # IUserRepository, IRoleRepository, IPermissionRepository（EF Core）
```

### 4.2 设计决策

| 决策 | 理由 |
|------|------|
| 按数据库分文件夹（APS/Auth） | 与三库架构对应，开发者一眼就知道某个仓储操作的是哪个库 |
| BaseRepository 提取到 Base/ | 重试逻辑、异常处理是跨库通用的，DRY 原则 |
| 重试仅对瞬态错误 | 死锁(1205)、超时(-2)、TCP错误(11001)等可重试；约束冲突(547)、主键重复(2627)等不可重试，避免无意义重试 |
| ScheduleRepository 事务修复 | DeleteScheduleResultAsync 中多表删除必须在同一事务内，使用 ExecuteInTransactionAsync 保证原子性 |

---

## 五、DI 自动注册（Scrutor）

### 5.1 问题

手动注册方式：
```csharp
services.AddScoped<IJobRepository, JobRepository>();
services.AddScoped<IMachineRepository, MachineRepository>();
// ... 每新增一个服务就要加一行，容易遗漏
```

### 5.2 解决方案

使用 [Scrutor](https://github.com/khellang/Scrutor) 按命名空间自动扫描：

| 层 | 扫描命名空间 | 生命周期 |
|---|-------------|---------|
| Engine | `Repositories.APS`, `Repositories.Auth`, `Services.Sync` | Scoped |
| Application | `Application.Services` | Scoped |
| BusinessRules | `BusinessRules.Rules` | Scoped |
| Scheduling | 手动注册（Singleton 纯算法，无接口约定） | Singleton |

### 5.3 收益

- **零配置**：新增服务只需创建 `IXxxService` + `XxxService`，Scrutor 自动发现
- **防遗漏**：不再有"写了实现但忘记注册"的问题
- **各层自治**：每个项目拥有自己的 `AddXxxServices` 扩展方法，团队成员不需要修改其他人的代码

### 5.4 约定

- 接口和实现必须在同一程序集的指定命名空间下
- 接口命名 `IXxxService` / `IXxxRepository` / `IXxxRule`
- 实现命名 `XxxService` / `XxxRepository` / `XxxRule`
- 抽象类（如 BaseRepository）自动跳过，不会被注册

---

## 六、Hangfire 定时任务

### 6.1 结构

- **配置**：`Web/Extensions/HangfireServiceExtensions.cs` — SQL Server 存储、服务器选项
- **任务注册**：`UseHangfireJobs()` 扩展方法 — 集中管理所有 RecurringJob

### 6.2 设计决策

| 决策 | 理由 |
|------|------|
| 任务注册从 Program.cs 抽出 | 保持 Program.cs 简洁，新增定时任务只改 HangfireServiceExtensions |
| 使用 APS 库存储 Job 数据 | 避免引入第四个数据库，Hangfire 元数据量极小 |
| 增量同步每小时 + 全量每日 | 增量保证时效性，全量保证数据一致性（补偿机制） |

---

## 七、Shared 层精简

### 7.1 清理前（15个源文件）

包含大量未使用的脚手架代码：DDD 基类（Entity/AggregateRoot/ValueObject）、通用仓储接口（IRepository<T>）、CQRS 接口、规约模式、自定义缓存抽象、增强Logger、自定义异常体系、工具类。

### 7.2 清理后（3个目录）

| 保留 | 理由 |
|------|------|
| `Models/APSModels.cs` | 被 12+ 个文件引用，是跨层通信的核心 DTO |
| `Configuration/` | appsettings.json 有对应配置节，启动时验证 |
| `Extensions/ServiceCollectionExtensions.cs` | Program.cs 调用 AddSharedServices |

### 7.3 为什么删除那些代码？

- **Core 实体不继承 Shared.Entity 基类**：实际实体直接映射数据库表，不需要 DDD 战术模式的 Id/Version/DomainEvents
- **实际仓储不实现 Shared.IRepository<T>**：APS 仓储是面向具体查询的（GetJobsByOrderId），不是 CRUD 泛型仓储
- **实际代码用标准 ILogger<T>**：自定义 EnhancedLogger 从未被注入
- **原则**：YAGNI（You Aren't Gonna Need It）—— 不要为"将来可能用"保留代码，它只会增加认知负担和维护成本

---

## 八、总结：核心设计原则

| 原则 | 在本项目中的体现 |
|------|----------------|
| **单一职责 (SRP)** | 每个项目只负责一件事：Engine 只做数据访问，Scheduling 只做算法 |
| **依赖倒置 (DIP)** | 上层依赖接口（IJobRepository），不依赖具体实现（JobRepository） |
| **接口隔离 (ISP)** | 每个仓储接口只暴露该聚合需要的方法，不是万能 CRUD |
| **YAGNI** | 删除 8 个未使用的脚手架文件，只保留实际使用的代码 |
| **Clean Architecture** | 严格单向依赖，Core 不依赖任何外层 |
| **防腐层** | ODS 契约视图隔离 ERP，APS 代码不直连外部系统 |
| **约定优于配置** | Scrutor 按命名空间约定自动注册，无需手动配置 |
| **按场景选型** | Dapper（性能敏感场景）+ EF Core（关系复杂场景），不追求统一 |

---

**编译状态**：7 个项目，0 错误，0 警告  
**技术栈**：.NET 8.0 / Dapper / EF Core 8 / Hangfire / Scrutor / SQL Server  
**架构搭建时间**：2026-04-03  
**最近重构时间**：2026-04-08

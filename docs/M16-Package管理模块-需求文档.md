# M16-Package管理模块-需求文档

## 1. 模块职责
- 负责：
  - 管理 Unity Package Manager (UPM) 中的包。
  - 支持通过名称、版本或 Git URL 添加包。
  - 支持移除已安装的包。
  - 列出当前项目中已安装的包并支持注册表查询。
- 不负责：
  - 不负责解决包之间的版本冲突。
  - 不负责修改 `manifest.json` 以外的项目配置。
  - 不负责下载包的二进制缓存管理。
- 输入/输出：
  - 输入：`package.add`, `package.remove`, `package.list`, `package.search` 命令。
  - 输出：包操作状态、已安装包列表、搜索结果。

## 2. 数据模型
| 字段名 | 类型 | 语义 | 合法范围 | 默认值 |
| --- | --- | --- | --- | --- |
| packageName | string | 包的唯一标识符 | 反向域名格式 | "" |
| version | string | 包的版本号 | 语义化版本或 Git 哈希 | "" |
| source | string | 包的来源 | registry/git/local/embedded | "registry" |
| displayName | string | 包的可视化名称 | 字符串 | "" |
| description | string | 包的功能描述 | 字符串 | "" |
| isInstalled | bool | 是否已安装在当前项目 | true/false | false |

## 3. CRUD 操作
| 操作 | 入口 | 禁用条件 | Command类名 | Undo语义 |
| --- | --- | --- | --- | --- |
| 添加包 | package.add | packageName为空 | AddPackageCmd | 不进 CommandHistory |
| 移除包 | package.remove | 包未安装 | RemovePackageCmd | 不进 CommandHistory |
| 查询已安装列表 | package.list | 无 | ListPackagesCmd | 不进 CommandHistory |
| 搜索注册表包 | package.search | 搜索关键词为空 | SearchPackagesCmd | 不进 CommandHistory |
| 刷新包状态 | 内部定时或事件触发 | 无 | RefreshPackageStatusCmd | 不进 CommandHistory |

## 4. 交互规格
- 触发事件：收到 package.add 后调用 Unity UPM 客户端 API。
- 状态变化：idle -> processing -> success/failed。
- 数据提交时机：操作开始即上报执行状态；操作完成后返回最终结果及其导致的编译状态变化。
- 取消/回退：不支持中途取消包下载或安装过程。

## 5. 视觉规格
不涉及。

## 6. 校验规则
| 规则 | 检查时机 | 级别 | 提示文案 |
| --- | --- | --- | --- |
| 禁止移除核心包 | 收到 package.remove 时 | Warning | 核心系统包建议保留 |
| Git URL 格式校验 | 执行 package.add 前 | Error | 无效的 Git 仓库地址 |
| 搜索关键词长度限制 | 收到 package.search 时 | Error | 关键词至少需要 3 个字符 |
| 版本号格式校验 | 写入 manifest 前 | Warning | 版本号可能不符合 SemVer 规范 |

## 7. 跨模块联动
| 模块 | 方向 | 说明 |
| --- | --- | --- |
| M02 UnityBridge通信模块 | 被动接收 | 接收包管理命令并回传执行结果 |
| M03 编译与错误收集模块 | 主动通知 | 包变更后触发编译状态检查 |
| M01 编排服务模块 | 被动接收 | 返回包列表信息用于后续逻辑判断 |

## 8. 技术实现要点
- 关键类与职责：
  - PackageService：封装 Unity Editor `Client` 类进行包操作。
  - PackageSearcher：处理注册表搜索与缓存。
  - PackageRegistryStore：管理当前项目已安装包的快照。
- 核心流程：package.add -> Unity UPM API call -> wait for progress -> asset database refresh -> return result。
- 性能约束：
  - 列表查询响应 < 20ms（从内存缓存读取）。
  - 搜索接口支持异步流式返回（若结果集过大）。
  - 禁止在主线程阻塞等待网络下载。

## 9. 验收标准
1. [空闲状态] -> [调用 package.list] -> [返回当前项目所有已安装包及其来源]。
2. [指定有效 Git URL] -> [调用 package.add] -> [Unity 开始导入包并触发编译]。
3. [输入不存在的包名] -> [调用 package.add] -> [返回错误码 404 及错误信息]。
4. [调用 package.remove 移除非核心包] -> [操作成功] -> [package.list 不再包含该包]。

## 10. 边界规范
- 空数据：项目无扩展包时 list 返回空数组 []。
- 单元素：项目仅安装 1 个包时返回长度为 1 的数组。
- 上下限临界值：支持同时列出超过 200 个包而无卡顿。
- 异常数据恢复：网络中断导致包下载失败时，自动恢复 manifest 到操作前状态。

## 11. 周边可选功能
- P1：依赖树可视化分析，预留 dependencyGraph 字段。
- P2：包版本自动更新提醒，预留 updateAvailable 标记。

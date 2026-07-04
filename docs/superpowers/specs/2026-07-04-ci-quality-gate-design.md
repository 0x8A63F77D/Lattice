# CI 与质量门禁设计（M2 之前）

日期：2026-07-04
状态：已批准

## 目标

在 M2 开发开始前，为 GitHub 仓库 `0x8A63F77D/Lattice` 配置持续集成与合并门禁：每个进入 `main` 的变更都必须通过完整构建（零警告）与全部测试。

## 决策记录

| 问题 | 决策 | 理由 |
|---|---|---|
| 范围 | 仅 CI + 门禁，不含发布流水线 | M2 是 UI 开发，短期无发包需求；NuGet 发布等有需求时再加（YAGNI） |
| 门禁强度 | `main` 仅 PR 合并 + required check，无管理员豁免 | 流程本来就走 PR（含 Codex Review）；逃生门 = 临时 disable ruleset |
| 运行平台 | 仅 `ubuntu-latest`，matrix 形式预留扩展 | 协议库纯托管代码；本地开发即 Windows，CI 补 Linux 侧；M2 出现平台相关代码再扩 |
| 检查内容 | Release 构建 `-warnaserror` + 全部测试 | 锁住当前零警告状态；格式检查/覆盖率阈值暂不加，被咬到再说 |
| 保护机制 | Repository ruleset（非老式 branch protection） | GitHub 当前推荐形态，可配置项更全，公开仓库免费 |

## 第 1 节：CI 工作流

文件：`.github/workflows/ci.yml`

- **触发**：`pull_request`（目标 `main`）+ `push`（`main` 分支）。合并后在 `main` 上再跑一次，防止 merge 产物与 PR 头部不一致。
- **Job：`build-test`**，`strategy.matrix.os: [ubuntu-latest]`。matrix 只有一项是有意的：M2 扩平台时加一行即可，job 显示名保持 `build-test` 稳定，required check 不需要重配。
- **步骤**：
  1. `actions/checkout`
  2. `actions/setup-dotnet`，SDK 版本 `10.0.x`
  3. `dotnet build Lattice.sln -c Release -warnaserror`
  4. `dotnet test Lattice.sln -c Release --no-build`
- **并发**：`concurrency: ci-${{ github.ref }}`，`cancel-in-progress: true`——同分支推新 commit 时取消旧运行。
- 用 Release 配置构建与测试，与未来发包口径一致。`-warnaserror` 只在 CI 生效，本地构建不受影响。

## 第 2 节：分支保护（ruleset）

对 `main` 启用一条 repository ruleset：

- 必须通过 PR 合并（`pull_request` 规则，approve 数 0——单人仓库要求 approve 会卡死流程）
- required status check：`build-test`
- 禁止 force push、禁止删除分支
- 不配置任何 bypass actor（管理员同样受约束）

逃生门：紧急情况下在 Settings → Rules 里临时将 ruleset 切为 disabled，事后恢复。这是显式操作，留有审计痕迹。

## 第 3 节：落地顺序

1. 从 `main` 拉分支提交 CI 工作流，开独立 PR（PR #2），合并进 `main`
2. 合并后启用 ruleset（顺序不能反：ruleset 先行会让 PR #2 因 required check 尚不存在而无法合并）
3. 在 PR #1（M1 分支）上重新触发 CI（推送空提交或 re-run），使其同样经过门禁后合并

## 测试策略

门禁本身需要验证一次真的拦得住：

- 推一个带编译警告的临时分支开 PR → 预期 CI 红（`-warnaserror` 生效）
- 推一个带失败测试的临时分支开 PR → 预期 CI 红
- 验证 `main` 无法直推（git push 被 ruleset 拒绝）
- 验证 CI 红的 PR 合并按钮被禁用
- 验证完删除临时分支

## 完成定义

- [ ] `ci.yml` 合并进 `main`，PR 与 `main` push 均触发
- [ ] ruleset 生效：`main` 禁直推、禁 force push，PR 必须 `build-test` 绿才能合并
- [ ] 门禁拦截行为按测试策略验证通过
- [ ] PR #1 在新门禁下 CI 绿

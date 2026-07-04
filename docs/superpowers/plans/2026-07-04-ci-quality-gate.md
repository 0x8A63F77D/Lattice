# CI 与质量门禁实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 为 `0x8A63F77D/Lattice` 配置 GitHub Actions CI 与 `main` 分支合并门禁：所有变更必须经 PR、Release 构建零警告、测试全过才能进 `main`。

**Architecture:** 单一 workflow（`ci.yml`，单 job `build-test`，matrix 形式仅 ubuntu-latest 一项）+ 一条 repository ruleset（PR-only、required check、禁 force push/删除、无豁免）。先合 CI，后开 ruleset，最后对现存 PR #1 重触发验证。

**Tech Stack:** GitHub Actions（actions/checkout、actions/setup-dotnet）、gh CLI（rulesets REST API）、.NET 10 SDK。

**Spec:** `docs/superpowers/specs/2026-07-04-ci-quality-gate-design.md`

## Global Constraints

- 每个新 shell 必须先执行：`export PATH="/c/Program Files/dotnet:/c/Program Files/GitHub CLI:$PATH" HTTPS_PROXY="http://192.168.1.192:10090" HTTP_PROXY="http://192.168.1.192:10090"`（Git Bash；gh 与 git push 都需要代理）
- 本机 dotnet 输出是中文本地化（成功行是 `已通过!`）；管道会掩盖退出码——判断成败看实际结果行，不看 `| tail` 后的退出码
- 工作目录：`D:/0x8A63F77D/Documents/GitHub/Lattice`；当前分支 `ci-quality-gate`（自 `origin/main` 拉出，已含 spec 提交）
- job 显示名固定为 `build-test`（required check 按此名匹配）；M2 扩 matrix 时需要改名并同步更新 ruleset，这是已知的将来动作
- 提交信息用 conventional commits；不要把临时验证分支合进任何东西

---

### Task 1: CI 工作流文件 + PR #2

**Files:**
- Create: `.github/workflows/ci.yml`

**Interfaces:**
- Produces: GitHub check context `build-test`（Task 3 的 ruleset 引用它；Task 4/5 观察它）

- [ ] **Step 1: 写 workflow 文件**

`.github/workflows/ci.yml` 内容如下（注意 job 的 `name` 是固定字符串，不带 matrix 插值——这保证 check 名稳定为 `build-test`）：

```yaml
name: CI

on:
  pull_request:
    branches: [main]
  push:
    branches: [main]

concurrency:
  group: ci-${{ github.ref }}
  cancel-in-progress: true

jobs:
  build-test:
    name: build-test
    strategy:
      matrix:
        os: [ubuntu-latest]
    runs-on: ${{ matrix.os }}
    steps:
      - uses: actions/checkout@v5
      - uses: actions/setup-dotnet@v4
        with:
          dotnet-version: 10.0.x
      - run: dotnet build Lattice.sln -c Release -warnaserror
      - run: dotnet test Lattice.sln -c Release --no-build
```

- [ ] **Step 2: 本地跑一遍同样的命令，确认当前代码在该口径下是绿的**

```bash
cd "D:/0x8A63F77D/Documents/GitHub/Lattice"
export PATH="/c/Program Files/dotnet:$PATH"
dotnet build Lattice.sln -c Release -warnaserror
dotnet test Lattice.sln -c Release --no-build
```

预期：构建无警告无错误；测试行显示 `已通过! - 失败: 0，通过: 53`（main 分支是 53 个测试；PR #1 的 57 个尚未合入）。

- [ ] **Step 3: 提交并推送**

```bash
git add .github/workflows/ci.yml
git commit -m "ci: add build-test workflow (Release, warnings as errors)"
git push -u origin ci-quality-gate
```

- [ ] **Step 4: 开 PR #2 并等 CI 结果**

```bash
gh pr create --base main --head ci-quality-gate \
  --title "ci: add CI workflow and quality gate spec" \
  --body "Adds .github/workflows/ci.yml (Release build with -warnaserror + full test suite on ubuntu-latest) per docs/superpowers/specs/2026-07-04-ci-quality-gate-design.md. Ruleset for main will be enabled after this merges.

🤖 Generated with [Claude Code](https://claude.com/claude-code)"
gh pr checks --watch
```

预期：`build-test` check 出现且最终 pass。若失败，读日志（`gh run view --log-failed`）修复后重推，直到绿。

- [ ] **Step 5: 无需额外提交**（本 task 交付物即 PR #2 及其绿色 check）

### Task 2: 合并 PR #2

**Files:** 无新文件（纯 GitHub 操作）

**Interfaces:**
- Consumes: Task 1 的 PR #2（check 已绿）
- Produces: `main` 上存在 `ci.yml`（Task 3 的 ruleset 依赖该 check 已在 `main` 存在）

- [ ] **Step 1: 确认 check 绿后合并（rebase 保留 docs 与 ci 两个提交）**

```bash
gh pr merge 2 --rebase --delete-branch
git checkout main && git pull
git log --oneline -3
```

预期:`main` 顶部出现 `ci:` 与 `docs:` 两个提交；本地 `ci-quality-gate` 分支已被 gh 删除。

- [ ] **Step 2: 确认 push 触发的 CI 在 main 上也绿**

```bash
gh run list --branch main --limit 1
```

预期：最新 run 状态为 `completed success`（可能需等待约 2-3 分钟，用 `gh run watch <id>` 跟踪）。

### Task 3: 启用 main 的 ruleset

**Files:** 无（GitHub 配置，经 REST API）

**Interfaces:**
- Consumes: check context `build-test`（Task 1 定义）
- Produces: `main` 的合并门禁（Task 4/5 验证其行为）

- [ ] **Step 1: 创建 ruleset**

```bash
gh api repos/0x8A63F77D/Lattice/rulesets --method POST --input - <<'EOF'
{
  "name": "protect-main",
  "target": "branch",
  "enforcement": "active",
  "conditions": { "ref_name": { "include": ["~DEFAULT_BRANCH"], "exclude": [] } },
  "rules": [
    { "type": "deletion" },
    { "type": "non_fast_forward" },
    { "type": "pull_request", "parameters": {
        "required_approving_review_count": 0,
        "dismiss_stale_reviews_on_push": false,
        "require_code_owner_review": false,
        "require_last_push_approval": false,
        "required_review_thread_resolution": false,
        "allowed_merge_methods": ["merge", "squash", "rebase"]
    } },
    { "type": "required_status_checks", "parameters": {
        "strict_required_status_checks_policy": false,
        "do_not_enforce_on_create": false,
        "required_status_checks": [ { "context": "build-test" } ]
    } }
  ],
  "bypass_actors": []
}
EOF
```

预期：返回 JSON 含 `"enforcement": "active"` 和一个 ruleset `id`。若 API 报参数错误（rulesets 参数 schema 偶有演进），用 `gh api repos/0x8A63F77D/Lattice/rulesets --method POST` 的报错信息对照官方文档调整字段名，目标配置不变：PR-only + required check `build-test` + 禁删除 + 禁 force push + 无 bypass。

- [ ] **Step 2: 验证直推被拒**

```bash
git checkout main
git commit --allow-empty -m "test: should be rejected"
git push 2>&1 | tail -5
git reset --hard origin/main
```

预期：push 被拒，错误信息提及 ruleset / pull request 要求（GH013 或类似）。**必须确认拒绝后再 reset**；若居然推上去了，说明 ruleset 没生效——立即 `git push` 一个 revert 并回查 Step 1。

### Task 4: 验证门禁拦截（红色 PR 不能合并）

**Files:**
- Create（临时，验证完删除）: 临时分支 `tmp/gate-check` 上对 `src/Lattice.Boinc.GuiRpc/XmlSanitizer.cs` 的破坏性修改

**Interfaces:**
- Consumes: Task 3 的 ruleset + Task 1 的 workflow

- [ ] **Step 1: 推一个带编译警告的分支**

```bash
git checkout -b tmp/gate-check origin/main
cat >> src/Lattice.Boinc.GuiRpc/XmlSanitizer.cs <<'EOF'

internal static class GateCheckWarning
{
    private static int _unused; // CS0169: intentionally unused to trip -warnaserror
}
EOF
git add -A && git commit -m "test: intentional warning (gate check, do not merge)"
git push -u origin tmp/gate-check
gh pr create --base main --head tmp/gate-check --title "test: gate check (do not merge)" --body "Verifies -warnaserror blocks merge. Will be closed."
gh pr checks --watch
```

预期：`build-test` **失败**（CS0169 警告被 `-warnaserror` 升级为错误）。

- [ ] **Step 2: 确认合并按钮被禁**

```bash
gh pr view --json mergeStateStatus --jq .mergeStateStatus
```

预期：`BLOCKED`。

- [ ] **Step 3: 换成失败测试再验一次**

```bash
git checkout tmp/gate-check
git reset --hard origin/main
cat > tests/Lattice.Tests/GateCheckTests.cs <<'EOF'
using Xunit;

namespace Lattice.Tests;

public class GateCheckTests
{
    [Fact]
    public void Intentionally_fails_to_verify_the_gate() => Assert.True(false);
}
EOF
git add -A && git commit -m "test: intentional failing test (gate check, do not merge)"
git push --force-with-lease
gh pr checks --watch
```

预期：`build-test` 失败（构建过、测试挂）；`mergeStateStatus` 仍为 `BLOCKED`。

- [ ] **Step 4: 清理**

```bash
gh pr close tmp/gate-check --delete-branch
git checkout main
git branch -D tmp/gate-check 2>/dev/null || true
```

预期：临时 PR 关闭，远端与本地临时分支删除。

### Task 5: PR #1 过新门禁

**Files:** 无新文件

**Interfaces:**
- Consumes: `m1-guirpc-protocol-layer` 分支（PR #1）、Task 3 的 ruleset

- [ ] **Step 1: 重触发 PR #1 的 CI**

workflow 合入 `main` 前 PR #1 没有触发过 CI，推一个空提交触发 `pull_request: synchronize`：

```bash
git checkout m1-guirpc-protocol-layer
git commit --allow-empty -m "ci: trigger checks under new quality gate"
git push
gh pr checks 1 --watch
```

预期：`build-test` pass（57 个测试）。

- [ ] **Step 2: 确认 PR #1 可合并状态**

```bash
gh pr view 1 --json mergeStateStatus --jq .mergeStateStatus
```

预期：`CLEAN`（或 `UNSTABLE` 之外的可合并状态）。合并本身留给用户在 GitHub 上操作（M1 的既定流程）。

- [ ] **Step 3: 更新记忆中的项目状态**

把 `lattice-project-status.md` 里补一行：CI + ruleset 已生效（日期、check 名 `build-test`、M2 扩 matrix 时需改 job 名并同步 ruleset）。

# P02 · 第6句第二载体缺 token

- **适用侧**：公共 ｜ **寿命**：现状 ｜ **最后校准**：2026-11-22 ｜ **状态**：未验

## 现状
切 `Actions:ActionSet=GiteaActionSet` 只改环境变量，`git diff src/Core/` 切前切后都为 0；能力目录真换了（`erp.query` → `spec_not_found`，`repo.query` → 识别并打到 GiteaAdapter）。gitea.com 公开实例可达（版本/仓库匿名读 200）。

## 卡在哪
`GiteaOptions.IsConfigured` 要 Token 非空；当前环境没有 gitea.com 只读 token，真查证返回对方 `401 not_authenticated`。

## 下一步
拿到一个 gitea.com 只读 token（走环境变量，不进 git），真跑一遍 `repo.query`。

## 判定权
第二体系自己的记录 + `git diff src/Core/` 为空。见 `docs/验收映射.md` 第 6 句。

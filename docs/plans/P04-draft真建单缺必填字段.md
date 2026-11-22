# P04 · draft 真建单缺必填字段

- **适用侧**：公共 ｜ **寿命**：现状 ｜ **最后校准**：2026-11-22 ｜ **状态**：未验

## 现状
MCP 绑定已补（`frappe_create_doc`，`amount → data.remarks`），离网判据绿。真跑被 Frappe 以 `417 + MandatoryError: supplier, items` 明确拒绝——绑定只写备注，建不了真单。

## 卡在哪
一张真采购单需要 supplier / items 等必填字段，那是业务语义重量，不是传输绑定。

## 下一步
要么把 Draft 的 InputSchema 扩成能收 supplier/items（改业务契约），要么承认草稿只在本地登记。这个取舍涉及产品边界，先定方向再动代码。

## 判定权
对方系统里有没有真的多出一张草稿单。

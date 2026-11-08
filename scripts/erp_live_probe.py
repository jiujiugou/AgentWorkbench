#!/usr/bin/env python3
"""ERPNext 只读采集 / 验证探针（M2 第 1 步）。

为什么要有这个脚本：
M2 的字段名、状态取值、错误状态码、时间格式全部由外部 ERPNext 决定，不采集就只能猜。
猜出来的失败分支 100% 是错的 —— 假 ERP 里我们可以随便叫 `document_id` / `amount`，
真 ERPNext 的采购单里没有 `amount`，只有 `grand_total` / `net_total` / 明细行上的 `rate`。
所以 M2 的第一个动作不是写适配器，而是把真实响应抓下来，变成可回放的夹具。
（依据：docs/设计/M2-真实ERPNext只读接入设计.md §2.2 / §9.4）

用法：
    python scripts/erp_live_probe.py --verify      # 只验证，不写文件；退出码即结论
    python scripts/erp_live_probe.py --record      # 采集 + 生成夹具 + 证据文件
    python scripts/erp_live_probe.py --record --probe-bad-auth   # 额外采集 401 形态
    python scripts/erp_live_probe.py --record --redact-host      # 证据里隐去主机名
    python scripts/erp_live_probe.py --record --insecure         # 自签名证书（仅本地实例）

环境变量：
    ERPNEXT_BASE_URL            例 http://localhost:8080（必填）
    ERPNEXT_API_KEY             （必填）
    ERPNEXT_API_SECRET          （必填）
    ERPNEXT_DOCTYPE             默认 "Purchase Order"
    ERPNEXT_TIMEZONE            覆盖站点时区；站点取不到时必须显式给，不许猜
    ERPNEXT_FORBIDDEN_API_KEY   可选：无读权限的账号，用于采集 403 形态
    ERPNEXT_FORBIDDEN_API_SECRET

三条规矩（M2 设计 §9.4）：
    1. 凭据只从环境变量读，不打印、不落文件；
    2. 夹具只能由本脚本生成，禁止手写；
    3. --verify 失败退出码非 0，不许「跑过就算」。

退出码：0 通过 / 1 验证失败 / 2 用法或配置错误 / 3 连不上实例
"""

from __future__ import annotations

import argparse
import json
import os
import ssl
import sys
import time
import urllib.error
import urllib.parse
import urllib.request
from datetime import datetime
from pathlib import Path
from typing import Any

ROOT = Path(__file__).resolve().parents[1]
DOCS_DIR = ROOT / "docs/验证与现状"
FIXTURE_DIR = ROOT / "tests/AgentWorkbench.ApplicationTests/Fixtures/erpnext"

DEFAULT_DOCTYPE = "Purchase Order"
TIMEOUT_SECONDS = 20

# 采集关注的字段。不是「全部字段」，是「缺了就没法判的那几个」（M2 设计 §7）。
KEY_FIELDS = [
    "name",
    "docstatus",
    "status",
    "company",
    "supplier",
    "grand_total",
    "net_total",
    "currency",
    "transaction_date",
    "modified",
    "creation",
    "owner",
]


# --------------------------------------------------------------------------- 基础设施


def out(msg: str = "") -> None:
    try:
        sys.stdout.write(msg + "\n")
        sys.stdout.flush()
    except UnicodeEncodeError:  # 极端终端编码下的兜底，不吞错误
        sys.stdout.write(msg.encode("utf-8", "replace").decode("utf-8", "replace") + "\n")


def die(code: int, msg: str) -> None:
    out(f"[错误] {msg}")
    sys.exit(code)


def build_opener(insecure: bool, use_proxy: bool) -> urllib.request.OpenerDirector:
    handlers: list[Any] = []
    # 本机 HTTP_PROXY 会劫持 localhost 请求（项目老坑），默认绕过代理。
    if not use_proxy:
        handlers.append(urllib.request.ProxyHandler({}))
    if insecure:
        ctx = ssl.create_default_context()
        ctx.check_hostname = False
        ctx.verify_mode = ssl.CERT_NONE
        handlers.append(urllib.request.HTTPSHandler(context=ctx))
    return urllib.request.build_opener(*handlers)


def redact_url(base: str, redact_host: bool) -> str:
    """脱敏：去掉 userinfo 与查询串；必要时隐去主机。"""
    parts = urllib.parse.urlsplit(base)
    host = parts.hostname or ""
    if redact_host:
        host = "<erp-host>"
    elif parts.port:
        host = f"{host}:{parts.port}"
    return urllib.parse.urlunsplit((parts.scheme, host, parts.path.rstrip("/"), "", ""))


class Client:
    def __init__(self, base: str, key: str, secret: str, insecure: bool, use_proxy: bool):
        self.base = base.rstrip("/")
        self.key = key
        self.secret = secret
        self.opener = build_opener(insecure, use_proxy)
        self.calls = 0

    def get(self, path: str, query: dict | None = None, key: str | None = None, secret: str | None = None) -> dict:
        """返回 {status, path, query, elapsed_ms, body, text, error}。HTTP 错误不抛，作为结果返回。"""
        url = self.base + path
        if query:
            url += "?" + urllib.parse.urlencode(query)
        token = f"{key if key is not None else self.key}:{secret if secret is not None else self.secret}"
        req = urllib.request.Request(
            url,
            headers={"Accept": "application/json", "Authorization": f"token {token}"},
        )
        started = time.monotonic()
        self.calls += 1
        status = 0
        text = ""
        error = None
        try:
            with self.opener.open(req, timeout=TIMEOUT_SECONDS) as resp:
                status = resp.status
                text = resp.read().decode("utf-8", "replace")
        except urllib.error.HTTPError as exc:  # 4xx/5xx 也是有效观测，不能当异常吞掉
            status = exc.code
            text = exc.read().decode("utf-8", "replace")
        except urllib.error.URLError as exc:
            error = f"{type(exc).__name__}: {exc.reason}"
        except Exception as exc:  # noqa: BLE001 - 采集器要如实记录任何失败
            error = f"{type(exc).__name__}: {exc}"
        elapsed_ms = int((time.monotonic() - started) * 1000)

        body: Any = None
        if text:
            try:
                body = json.loads(text)
            except json.JSONDecodeError:
                body = None
        return {
            "status": status,
            "path": path,
            "query": query,
            "elapsed_ms": elapsed_ms,
            "body": body,
            "text": text[:4000],
            "error": error,
        }


def scrub(value: Any, secrets: list[str]) -> Any:
    """把凭据字符串从任何落盘内容里抹掉（参数里理论上不会有，但便宜且值得）。"""
    if isinstance(value, str):
        for s in secrets:
            if s and s in value:
                value = value.replace(s, "***")
        return value
    if isinstance(value, dict):
        return {k: scrub(v, secrets) for k, v in value.items()}
    if isinstance(value, list):
        return [scrub(v, secrets) for v in value]
    return value


def frappe_error_hint(result: dict) -> str:
    """Frappe 的错误信息常藏在 exc_type / _server_messages / exception 里。"""
    body = result.get("body")
    if not isinstance(body, dict):
        return result.get("text", "")[:200]
    for key in ("exc_type", "exception", "_server_messages", "message"):
        if key in body and body[key]:
            return f"{key}={str(body[key])[:200]}"
    return result.get("text", "")[:200]


# --------------------------------------------------------------------------- 采集步骤


def probe_identity(client: Client) -> dict:
    result = client.get("/api/method/frappe.auth.get_logged_user")
    user = None
    if isinstance(result.get("body"), dict):
        user = result["body"].get("message")
    return {"probe": "identity", "result": result, "user": user}


def probe_versions(client: Client) -> dict:
    result = client.get("/api/method/frappe.utils.change_log.get_versions")
    versions = None
    if isinstance(result.get("body"), dict):
        versions = result["body"].get("message")
    return {"probe": "versions", "result": result, "versions": versions}


def probe_timezone(client: Client) -> dict:
    """站点时区有三个候选来源，谁先成功用谁；全失败就是「取不到」，不许猜。"""
    candidates = [
        (
            "frappe.client.get_single_value",
            "/api/method/frappe.client.get_single_value",
            {"doctype": "System Settings", "field": "time_zone"},
        ),
        (
            "frappe.client.get_value",
            "/api/method/frappe.client.get_value",
            {"doctype": "System Settings", "fieldname": "time_zone"},
        ),
        ("resource_system_settings", "/api/resource/System Settings", None),
    ]
    attempts = []
    timezone = None
    for name, path, query in candidates:
        result = client.get(path, query)
        value = None
        body = result.get("body")
        if isinstance(body, dict):
            value = body.get("message")
            if isinstance(value, dict):
                value = value.get("time_zone")
            if value is None:
                data = body.get("data")
                if isinstance(data, dict):
                    value = data.get("time_zone")
        attempts.append({"candidate": name, "status": result["status"], "value": value})
        if isinstance(value, str) and value.strip():
            timezone = value.strip()
            break
    return {"probe": "timezone", "time_zone": timezone, "attempts": attempts}


def probe_doctype_fields(client: Client, doctype: str) -> dict:
    path = "/api/resource/DocType/" + urllib.parse.quote(doctype, safe="")
    result = client.get(path)
    fields: list[dict] = []
    body = result.get("body")
    if isinstance(body, dict):
        data = body.get("data")
        if isinstance(data, dict) and isinstance(data.get("fields"), list):
            for f in data["fields"]:
                if not isinstance(f, dict):
                    continue
                fields.append(
                    {
                        "fieldname": f.get("fieldname"),
                        "fieldtype": f.get("fieldtype"),
                        "label": f.get("label"),
                        "options": f.get("options"),
                        "reqd": f.get("reqd"),
                        "is_custom_field": f.get("is_custom_field") or f.get("custom_field"),
                    }
                )
    return {"probe": "doctype_fields", "result": result, "fields": fields}


def list_names(client: Client, doctype: str, docstatus: int, limit: int = 3) -> dict:
    """按 docstatus 取候选单号。filters 用 JSON 数组编码，这是 Frappe 的约定。"""
    path = "/api/resource/" + urllib.parse.quote(doctype, safe="")
    query = {
        "filters": json.dumps([["docstatus", "=", docstatus]]),
        "fields": json.dumps(["name", "docstatus", "status", "modified"]),
        "limit_page_length": limit,
        "order_by": "modified desc",
    }
    result = client.get(path, query)
    rows: list[dict] = []
    body = result.get("body")
    if isinstance(body, dict) and isinstance(body.get("data"), list):
        rows = [r for r in body["data"] if isinstance(r, dict)]
    return {"probe": f"list_docstatus_{docstatus}", "result": result, "rows": rows}


def get_doc(client: Client, doctype: str, name: str) -> dict:
    path = "/api/resource/" + urllib.parse.quote(doctype, safe="") + "/" + urllib.parse.quote(name, safe="")
    return {"probe": "doc", "result": client.get(path), "name": name}


def probe_not_found(client: Client, doctype: str) -> dict:
    name = "__m2_probe_missing__-0000"
    path = "/api/resource/" + urllib.parse.quote(doctype, safe="") + "/" + urllib.parse.quote(name, safe="")
    return {"probe": "not_found", "result": client.get(path), "name": name}


def probe_unauthorized(base: str, insecure: bool, use_proxy: bool) -> dict:
    """错误凭据：只发一次，验证「401 不重试」这条语义的前提。"""
    bogus = Client(base, "m2_probe_invalid_key", "m2_probe_invalid_secret", insecure, use_proxy)
    return {"probe": "unauthorized", "result": bogus.get("/api/method/frappe.auth.get_logged_user")}


def probe_forbidden(base: str, key: str, secret: str, doctype: str, insecure: bool, use_proxy: bool) -> dict:
    """无权限账号读同一张单，用来区分 403 与 404——两者混淆会让越权探测变得可行。"""
    client = Client(base, key, secret, insecure, use_proxy)
    listing = list_names(client, doctype, 1, limit=1)
    if not listing["rows"]:
        listing = list_names(client, doctype, 0, limit=1)
    if not listing["rows"]:
        return {"probe": "forbidden", "skipped": "无候选单号，无法构造 403 场景", "result": listing["result"]}
    name = str(listing["rows"][0].get("name", ""))
    doc = get_doc(client, doctype, name)
    return {"probe": "forbidden", "result": doc["result"], "name": name}


def probe_filter_semantics(client: Client, doctype: str, expected_docstatus: int) -> dict:
    """确认 filters 真的生效：回来的每一行 docstatus 都必须等于请求值。
    这条比「接口 200」有用得多——filters 编码写错时 Frappe 常常照样返回 200，只是过滤没生效。
    """
    listing = list_names(client, doctype, expected_docstatus, limit=5)
    rows = listing["rows"]
    mismatched = [r for r in rows if r.get("docstatus") != expected_docstatus]
    return {
        "probe": "filter_semantics",
        "expected_docstatus": expected_docstatus,
        "result": listing["result"],
        "rows": rows,
        "mismatched": mismatched,
        "honored": bool(rows) and not mismatched,
    }


# --------------------------------------------------------------------------- 组装


def collect(doctype: str, base: str, insecure: bool, use_proxy: bool, probe_bad_auth: bool,
            forbidden_creds: tuple[str, str] | None, timezone_override: str | None) -> dict:
    key = os.environ["ERPNEXT_API_KEY"]
    secret = os.environ["ERPNEXT_API_SECRET"]
    client = Client(base, key, secret, insecure, use_proxy)

    identity = probe_identity(client)
    if identity["result"]["status"] != 200:
        detail = identity["result"]["error"] or frappe_error_hint(identity["result"])
        die(3, f"连不上或凭据无效（HTTP {identity['result']['status']}）：{detail}")

    out(f"[ok] 身份可达：{identity['user']}")
    versions = probe_versions(client)
    tz = probe_timezone(client)
    fields = probe_doctype_fields(client, doctype)
    out(f"[ok] DocType 字段采集：{len(fields['fields'])} 个" if fields["fields"] else "[warn] 字段表取不到（可能需要 DocType 读权限）")

    docs: dict[str, dict] = {}
    for label, docstatus in (("draft", 0), ("submitted", 1), ("cancelled", 2)):
        listing = list_names(client, doctype, docstatus)
        if not listing["rows"]:
            docs[label] = {"probe": "doc", "skipped": f"实例里没有 docstatus={docstatus} 的单据", "listing": listing}
            out(f"[skip] docstatus={docstatus} 无样本")
            continue
        name = str(listing["rows"][0].get("name", ""))
        doc = get_doc(client, doctype, name)
        doc["listing"] = listing
        docs[label] = doc
        out(f"[ok] docstatus={docstatus} 样本：{name}（HTTP {doc['result']['status']}）")

    not_found = probe_not_found(client, doctype)
    out(f"[ok] 不存在单号：HTTP {not_found['result']['status']} {frappe_error_hint(not_found['result'])[:80]}")

    unauthorized = probe_unauthorized(base, insecure, use_proxy) if probe_bad_auth else None
    if unauthorized:
        out(f"[ok] 错误凭据：HTTP {unauthorized['result']['status']}")

    forbidden = None
    if forbidden_creds:
        forbidden = probe_forbidden(base, forbidden_creds[0], forbidden_creds[1], doctype, insecure, use_proxy)
        out(f"[ok] 无权限账号：HTTP {forbidden['result']['status']}")

    filter_check = probe_filter_semantics(client, doctype, 1 if docs.get("submitted", {}).get("result") else 0)
    out(f"[{'ok' if filter_check['honored'] else 'warn'}] filters 生效性：{[r.get('docstatus') for r in filter_check['rows']]}")

    site_version = None
    if isinstance(versions["versions"], dict):
        # 只留 ERPNext / frappe 两个关键项，避免整包 app 列表污染证据
        for app in ("erpnext", "frappe"):
            if app in versions["versions"]:
                site_version = site_version or {}
                site_version[app] = versions["versions"][app]

    return {
        "captured_at": datetime.now().astimezone().isoformat(timespec="seconds"),
        "base_url": redact_url(base, redact_host=REDACT_HOST),
        "doctype": doctype,
        "integration_user": identity["user"],
        "site_version": site_version or "未采集",
        "versions_raw_status": versions["result"]["status"],
        "time_zone": timezone_override or tz["time_zone"] or "未采集",
        "time_zone_source": "ENV 覆盖" if timezone_override else ("站点查询" if tz["time_zone"] else "取不到"),
        "time_zone_attempts": tz["attempts"],
        "field_count": len(fields["fields"]),
        "doctype_fields": fields["fields"],
        "doctype_fields_raw": fields["result"],
        "documents": docs,
        "not_found": not_found,
        "unauthorized": unauthorized,
        "forbidden": forbidden,
        "filter_semantics": filter_check,
        "http_calls": client.calls,
        "secrets": [secret] + ([forbidden_creds[1]] if forbidden_creds else []),
    }


REDACT_HOST = False


def key_field_table(data: dict) -> list[tuple[str, str]]:
    rows = []
    for field in KEY_FIELDS:
        if field in data:
            value = data[field]
            rows.append((field, f"`{value}`" if not isinstance(value, (dict, list)) else "(复杂值)"))
        else:
            rows.append((field, "**缺失**"))
    return rows


def render_markdown(collected: dict) -> str:
    doctype = collected["doctype"]
    lines: list[str] = []
    lines.append(f"# ERPNext 采集记录（{doctype}）")
    lines.append("")
    lines.append("> 本文件由 `scripts/erp_live_probe.py --record` 生成，**不要手改**。")
    lines.append("> 它是 M2 的输入证据，不是完成标志；字段与错误码的结论要写进 M2 设计 §2.2 / §6。")
    lines.append("")
    lines.append("## 1 站点")
    lines.append("")
    lines.append("| 项 | 值 |")
    lines.append("|---|---|")
    lines.append(f"| 采集时间 | {collected['captured_at']} |")
    lines.append(f"| Base URL | `{collected['base_url']}` |")
    lines.append(f"| 集成账号 | `{collected['integration_user']}` |")
    lines.append(f"| 站点版本 | `{json.dumps(collected['site_version'], ensure_ascii=False)}` |")
    lines.append(f"| 站点时区 | `{collected['time_zone']}`（{collected['time_zone_source']}） |")
    lines.append(f"| DocType 字段数 | {collected['field_count']} |")
    lines.append(f"| HTTP 调用次数 | {collected['http_calls']} |")
    lines.append("")

    lines.append("## 2 三张样本单的关键字段")
    lines.append("")
    for label, title in (("draft", "Draft（docstatus=0）"), ("submitted", "Submitted（docstatus=1）"), ("cancelled", "Cancelled（docstatus=2）")):
        doc = collected["documents"].get(label) or {}
        lines.append(f"### {title}")
        lines.append("")
        if doc.get("skipped"):
            lines.append(f"未采集：{doc['skipped']}")
            lines.append("")
            continue
        body = (doc.get("result") or {}).get("body") or {}
        data = body.get("data") if isinstance(body, dict) else None
        if not isinstance(data, dict):
            lines.append(f"未采集：HTTP {(doc.get('result') or {}).get('status')} {frappe_error_hint(doc.get('result') or {})}")
            lines.append("")
            continue
        lines.append("| 字段 | 值 |")
        lines.append("|---|---|")
        for field, value in key_field_table(data):
            lines.append(f"| `{field}` | {value} |")
        lines.append("")
        raw_modified = data.get("modified")
        lines.append(f"时间原始串：`modified = {raw_modified}` —— {'**无时区偏移，需按站点时区转换**' if isinstance(raw_modified, str) and ('+' not in raw_modified and 'Z' not in raw_modified) else '已带偏移'}。")
        lines.append("")

    lines.append("## 3 失败形态实测")
    lines.append("")
    lines.append("| 场景 | HTTP | ERP 关键信息 | 我们应映射成 |")
    lines.append("|---|---|---|---|")
    nf = collected["not_found"]["result"]
    lines.append(f"| 单号不存在 | {nf['status']} | `{frappe_error_hint(nf)[:120]}` | `erp_not_found` |")
    if collected.get("unauthorized"):
        un = collected["unauthorized"]["result"]
        lines.append(f"| 凭据无效 | {un['status']} | `{frappe_error_hint(un)[:120]}` | `erp_auth_failed` |")
    else:
        lines.append("| 凭据无效 | 未采集 | 用 `--probe-bad-auth` 重跑 | `erp_auth_failed` |")
    if collected.get("forbidden"):
        fb = collected["forbidden"]["result"]
        if collected["forbidden"].get("skipped"):
            lines.append(f"| 无权限 | 未采集 | {collected['forbidden']['skipped']} | `erp_forbidden` |")
        else:
            lines.append(f"| 无权限 | {fb['status']} | `{frappe_error_hint(fb)[:120]}` | `erp_forbidden` |")
    else:
        lines.append("| 无权限 | 未采集 | 需 `ERPNEXT_FORBIDDEN_API_KEY/SECRET` | `erp_forbidden` |")
    lines.append("")

    lines.append("## 4 filters 生效性")
    lines.append("")
    fs = collected["filter_semantics"]
    lines.append(f"请求 `docstatus={fs['expected_docstatus']}`，返回行的 docstatus：`{[r.get('docstatus') for r in fs['rows']]}` → **{'生效' if fs['honored'] else '未生效或样本不足'}**。")
    lines.append("")
    lines.append("> 这条必须看：filters 的 JSON 编码写错时，Frappe 常常照样返回 200，只是过滤没生效。")
    lines.append("")

    lines.append("## 5 待人工判读（脚本不替你下结论）")
    lines.append("")
    lines.append("1. **字段漂移**：§2 里标 `**缺失**` 的字段，就是我们原先假设错了的点（M2 设计 §7 的必填项）。")
    lines.append("2. **幂等线索**：`doctype_fields` 里有没有 `request_id` / `external_ref` 一类字段？没有 → §8 问题 1 的答案是「不能按外部号反查」。")
    lines.append("3. **状态取值**：三张样本单的 `status` 字符串是否与预期一致？记下来，**不要**据此写死枚举。")
    lines.append("4. **时区**：站点时区是否已取到？取不到就必须在 `ErpNext:TimeZone` 显式配置，不许猜。")
    lines.append("")
    lines.append("## 6 夹具")
    lines.append("")
    lines.append(f"同一批响应已写入 `{FIXTURE_DIR.relative_to(ROOT).as_posix()}/`，带 `__meta__`（采集时间 / 站点版本 / 时区 / kind）。")
    lines.append("回放测试只认这批文件；**手写的夹具一律视为无效**。")
    lines.append("")
    return "\n".join(lines) + "\n"


def write_fixtures(collected: dict) -> list[str]:
    FIXTURE_DIR.mkdir(parents=True, exist_ok=True)
    meta_base = {
        "captured_at": collected["captured_at"],
        "site_version": collected["site_version"],
        "time_zone": collected["time_zone"],
        "doctype": collected["doctype"],
        "source": "erp_live_probe.py --record",
    }
    written: list[str] = []

    def dump(name: str, kind: str, result: dict, extra: dict | None = None) -> None:
        payload: dict[str, Any] = {
            "__meta__": {**meta_base, "kind": kind},
            "http_status": result.get("status"),
            "path": result.get("path"),
            "query": result.get("query"),
            "body": result.get("body"),
        }
        if extra:
            payload["__meta__"].update(extra)
        payload = scrub(payload, collected["secrets"])
        target = FIXTURE_DIR / name
        target.write_text(json.dumps(payload, ensure_ascii=False, indent=2, sort_keys=False), encoding="utf-8")
        written.append(name)

    for label, doc in collected["documents"].items():
        if doc.get("skipped"):
            continue
        dump(f"doc_{label}.json", f"doc_{label}", doc["result"], {"document_name": doc.get("name")})

    dump("error_not_found.json", "error_not_found", collected["not_found"]["result"],
         {"document_name": collected["not_found"].get("name")})
    if collected.get("unauthorized"):
        dump("error_unauthorized.json", "error_unauthorized", collected["unauthorized"]["result"])
    if collected.get("forbidden") and not collected["forbidden"].get("skipped"):
        dump("error_forbidden.json", "error_forbidden", collected["forbidden"]["result"],
             {"document_name": collected["forbidden"].get("name")})
    dump("list_filter_docstatus.json", "list_filter_docstatus", collected["filter_semantics"]["result"],
         {"expected_docstatus": collected["filter_semantics"]["expected_docstatus"]})
    # DocType 字段表也必须是原始响应，不能是脚本重组的对象（否则「夹具=真实响应」这句话就不成立）
    dump("doctype_fields.json", "doctype_fields", collected["doctype_fields_raw"])
    return written


def write_evidence(collected: dict) -> tuple[Path, Path]:
    DOCS_DIR.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d")
    evidence = {k: v for k, v in collected.items() if k != "secrets"}
    evidence_json = DOCS_DIR / f"erpnext-live-{stamp}.json"
    evidence_md = DOCS_DIR / f"erpnext-采集-{stamp}.md"
    evidence_json.write_text(json.dumps(scrub(evidence, collected["secrets"]), ensure_ascii=False, indent=2), encoding="utf-8")
    evidence_md.write_text(render_markdown(collected), encoding="utf-8")
    return evidence_json, evidence_md


# --------------------------------------------------------------------------- 验证模式


def verify(doctype: str, base: str, insecure: bool, use_proxy: bool,
           forbidden_creds: tuple[str, str] | None) -> int:
    client = Client(base, os.environ["ERPNEXT_API_KEY"], os.environ["ERPNEXT_API_SECRET"], insecure, use_proxy)
    checks: list[tuple[str, bool, str]] = []

    identity = probe_identity(client)
    ok = identity["result"]["status"] == 200 and bool(identity["user"])
    if ok:
        detail = f"HTTP 200 user={identity['user']}"
    elif identity["result"].get("error"):
        detail = f"连不上：{identity['result']['error']}"
    else:
        detail = f"HTTP {identity['result']['status']} {frappe_error_hint(identity['result'])}"
    checks.append(("V1 身份可达", ok, detail))
    if not ok:
        return report(checks)

    doc_ok = False
    found_docstatus = 0
    doc_detail = "三个 docstatus 都没有样本"
    for docstatus in (1, 0, 2):
        listing = list_names(client, doctype, docstatus, limit=1)
        if not listing["rows"]:
            continue
        name = str(listing["rows"][0].get("name", ""))
        doc = get_doc(client, doctype, name)
        body = doc["result"].get("body") or {}
        data = body.get("data") if isinstance(body, dict) else None
        if doc["result"]["status"] == 200 and isinstance(data, dict):
            has_name = bool(data.get("name"))
            has_docstatus = data.get("docstatus") is not None
            has_modified = bool(data.get("modified"))
            doc_ok = has_name and has_docstatus and has_modified
            found_docstatus = docstatus
            doc_detail = f"{name} docstatus={data.get('docstatus')} modified={data.get('modified')} 字段齐全={doc_ok}"
        else:
            doc_detail = f"{name} HTTP {doc['result']['status']} {frappe_error_hint(doc['result'])}"
        break
    checks.append(("V2 真实单据可读且关键字段齐全", doc_ok, doc_detail))

    nf = probe_not_found(client, doctype)
    nf_ok = nf["result"]["status"] in (404, 403)
    nf_detail = f"HTTP {nf['result']['status']} {frappe_error_hint(nf['result'])[:100]}"
    if nf["result"]["status"] == 403:
        nf_detail += "（权限先于存在性判，需与 M2 §6 的映射表对齐）"
    if nf["result"]["status"] == 200:
        nf_detail += " —— 返回 200，说明「查不到」需要靠 body 判，失败语义要改"
    checks.append(("V3 不存在的单号被明确拒绝（404/403）", nf_ok, nf_detail))

    fs = probe_filter_semantics(client, doctype, found_docstatus)
    checks.append(("V4 filters 生效（返回行 docstatus 与请求一致）", fs["honored"],
                   f"rows={[r.get('docstatus') for r in fs['rows']]}"))

    if forbidden_creds:
        fb = probe_forbidden(base, forbidden_creds[0], forbidden_creds[1], doctype, insecure, use_proxy)
        if fb.get("skipped"):
            checks.append(("V5 无权限与不存在可区分", False, str(fb["skipped"])))
        else:
            distinct = fb["result"]["status"] == 403
            checks.append(("V5 无权限与不存在可区分", distinct,
                           f"无权限账号读同一张单 HTTP {fb['result']['status']}，应与 404 不同"))

    return report(checks)


def report(checks: list[tuple[str, bool, str]]) -> int:
    out("")
    out("检查项                                             结果   详情")
    out("-" * 100)
    for name, ok, detail in checks:
        out(f"{name:<46} {'PASS' if ok else 'FAIL':<6} {detail}")
    failed = [c for c in checks if not c[1]]
    out("")
    if failed:
        out(f"结论：{len(checks) - len(failed)}/{len(checks)} 通过 —— live 验证未通过，M2 不能算完成。")
        return 1
    out(f"结论：{len(checks)}/{len(checks)} 通过。")
    return 0


# --------------------------------------------------------------------------- 入口


def read_env_creds(prefix: str) -> tuple[str, str] | None:
    key = os.environ.get(f"{prefix}_API_KEY")
    secret = os.environ.get(f"{prefix}_API_SECRET")
    if key and secret:
        return key, secret
    return None


def main(argv: list[str] | None = None) -> int:
    parser = argparse.ArgumentParser(
        description="ERPNext 只读采集 / 验证探针（M2 第 1 步）",
        formatter_class=argparse.RawDescriptionHelpFormatter,
        epilog="凭据只从环境变量读：ERPNEXT_BASE_URL / ERPNEXT_API_KEY / ERPNEXT_API_SECRET",
    )
    mode = parser.add_mutually_exclusive_group()
    mode.add_argument("--record", action="store_true", help="采集并生成夹具与证据文件")
    mode.add_argument("--verify", action="store_true", help="只验证（默认），不写文件")
    parser.add_argument("--doctype", default=os.environ.get("ERPNEXT_DOCTYPE", DEFAULT_DOCTYPE))
    parser.add_argument("--probe-bad-auth", action="store_true", help="额外采集错误凭据的 401 形态（只发一次请求）")
    parser.add_argument("--redact-host", action="store_true", help="证据里把主机名隐去")
    parser.add_argument("--insecure", action="store_true", help="跳过 TLS 校验（仅本地自签名实例）")
    parser.add_argument("--use-proxy", action="store_true", help="走系统代理（默认绕过，本机代理会劫持 localhost）")
    args = parser.parse_args(argv)

    global REDACT_HOST
    REDACT_HOST = args.redact_host

    base = os.environ.get("ERPNEXT_BASE_URL", "").strip()
    key = os.environ.get("ERPNEXT_API_KEY", "").strip()
    secret = os.environ.get("ERPNEXT_API_SECRET", "").strip()
    missing = [n for n, v in (("ERPNEXT_BASE_URL", base), ("ERPNEXT_API_KEY", key), ("ERPNEXT_API_SECRET", secret)) if not v]
    if missing:
        die(2, "缺少环境变量：" + ", ".join(missing) + "\n       例：ERPNEXT_BASE_URL=http://localhost:8080 ERPNEXT_API_KEY=xxx ERPNEXT_API_SECRET=yyy")

    forbidden_creds = read_env_creds("ERPNEXT_FORBIDDEN")
    timezone_override = os.environ.get("ERPNEXT_TIMEZONE", "").strip() or None

    out(f"目标：{redact_url(base, REDACT_HOST)}  DocType：{args.doctype}  模式：{'record' if args.record else 'verify'}")

    if not args.record:
        return verify(args.doctype, base, args.insecure, args.use_proxy, forbidden_creds)

    collected = collect(args.doctype, base, args.insecure, args.use_proxy,
                        args.probe_bad_auth, forbidden_creds, timezone_override)
    fixtures = write_fixtures(collected)
    evidence_json, evidence_md = write_evidence(collected)

    out("")
    out(f"[写] 夹具 {len(fixtures)} 个 → {FIXTURE_DIR.relative_to(ROOT).as_posix()}/")
    for name in fixtures:
        out(f"      {name}")
    out(f"[写] 证据 → {evidence_json.relative_to(ROOT).as_posix()}")
    out(f"[写] 采集记录 → {evidence_md.relative_to(ROOT).as_posix()}")
    out("")
    out("下一步：按采集记录 §5 人工判读字段漂移，再改 M2 设计 §6 / §7。")
    return 0


if __name__ == "__main__":
    sys.exit(main())

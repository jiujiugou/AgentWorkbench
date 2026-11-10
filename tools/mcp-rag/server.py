"""C# 主服务通过 MCP 启用的本地知识检索工具（只读）。

启动方是 D:/Code/AgentWorkbench 的 Webapi 进程：appsettings.json 的 `Mcp:Servers`
把本文件作为 stdio 子进程拉起，工具经 `ToolCatalog` 白名单后交给模型。

## 边界（ADR 0013）

这里**只做检索执行**，不做引用校验、不做拒答判定、不写任何业务状态。
判定留在 C#（`CitationVerifier` / `SufficiencyGate`），证据快照也落在 C#。
返回的每条 hit 都带 chunkId / documentId / fileName / 页码，是为了让调用方
能按同一顺序还原 `[n]`、并写下当时看到的原文，而不是替它做决定。

## 三条硬约束

1. **stdout 是 JSON-RPC 协议通道**。往 stdout 打一行日志，整条连接当场废掉
   （客户端解析失败）。所有诊断信息一律写 stderr。
2. **只读连接**（`mode=ro`）。不写库、不改索引、不重建向量；连不上返回结构化
   错误而不是抛异常，避免一次检索把整个 server 进程搞崩。
3. **索引与查询必须用同一个分词器**。C# 写入侧是 bigram，这里若用 unicode61
   直接 MATCH 中文整词，结果是 0 命中且不报任何错——这正是最难查的那类失败。
"""

from __future__ import annotations

import json
import os
import re
import sqlite3
import sys
from pathlib import Path
from typing import Any

import anyio
from mcp.server.mcpserver import MCPServer

# tools/mcp-rag/server.py → 仓库根
_REPO_ROOT = Path(__file__).resolve().parents[2]
_DEFAULT_DB = _REPO_ROOT / "src" / "Host" / "AgentWorkbench.Webapi" / "data" / "agentworkbench.db"

# 与 C# BigramTokenizer.SegmentPattern 逐字一致
_SEGMENT = re.compile(r"[A-Za-z0-9_#](?:[A-Za-z0-9_#.\-/]*[A-Za-z0-9_#])?|[\u4e00-\u9fff]+")
_MAX_QUERY_TOKENS = 24

server = MCPServer(
    "agentworkbench-rag",
    description="本地企业资料检索（只读）。返回带出处的片段，不做答案生成与引用判定。",
)


def _log(message: str) -> None:
    print(f"[mcp-rag] {message}", file=sys.stderr, flush=True)


def _db_path() -> Path:
    raw = os.environ.get("AWB_DB_PATH")
    return Path(raw) if raw else _DEFAULT_DB


def _tokens(query: str) -> list[str]:
    """中文连续段逐字生成 bigram；ASCII 段整体入 token。单字不成 bigram。

    去重保序后截到 24 个，与 C# HybridRetriever 的构造一致，保证两边召回可比。
    """
    result: list[str] = []
    seen: set[str] = set()
    for match in _SEGMENT.finditer(query):
        run = match.group(0)
        pieces = [run] if ord(run[0]) < 0x80 else [run[i : i + 2] for i in range(len(run) - 1)]
        for token in pieces:
            if token in seen:
                continue
            seen.add(token)
            result.append(token)
            if len(result) >= _MAX_QUERY_TOKENS:
                return result
    return result


def _escape(token: str) -> str:
    return '"' + token.replace('"', '""') + '"'


def _hit_payload(
    conn: sqlite3.Connection, row: sqlite3.Row, no: int
) -> dict[str, Any] | None:
    """回表取正文与出处。回表查不到就丢——FTS 里残留的孤儿 chunk 不该进结果。

    字段名用 camelCase：C# 侧 `RetrievalHit` + JsonSerializerDefaults.Web 可直接
    反序列化，不需要中间 DTO。`no` 是模型写 [n] 用的编号；服务端按「下标 + 1」
    还原同一顺序，两边必须对得上，否则引用会被判成越界。
    """
    chunk = conn.execute(
        """
        SELECT c.id, c.document_id, c.seq, c.page_from, c.page_to, c.heading, c.content,
               d.file_name
        FROM chunks c LEFT JOIN documents d ON d.id = c.document_id
        WHERE c.id = ?
        """,
        (row["chunk_id"],),
    ).fetchone()
    if chunk is None:
        return None
    return {
        "no": no,
        "chunkId": chunk["id"],
        "documentId": chunk["document_id"],
        "fileName": chunk["file_name"] or "",
        "seq": chunk["seq"],
        "heading": chunk["heading"],
        "pageFrom": chunk["page_from"],
        "pageTo": chunk["page_to"],
        "content": chunk["content"],
        "textScore": float(row["score"]),
    }


def _search(query: str, top_k: int) -> dict[str, Any]:
    db = _db_path()
    if not db.is_file():
        return {"error": "database_not_found", "path": str(db), "hits": []}

    tokens = _tokens(query)
    try:
        conn = sqlite3.connect(f"file:{db.as_posix()}?mode=ro", uri=True)
        conn.row_factory = sqlite3.Row
    except sqlite3.Error as exc:
        return {"error": "database_open_failed", "detail": str(exc), "hits": []}

    try:
        rows: list[sqlite3.Row] = []
        if tokens:
            match = " OR ".join(_escape(t) for t in tokens)
            rows = conn.execute(
                """
                SELECT chunk_id, document_id, -bm25(chunks_fts) AS score
                FROM chunks_fts WHERE chunks_fts MATCH ?
                ORDER BY bm25(chunks_fts) LIMIT ?
                """,
                (match, top_k),
            ).fetchall()

        # 单字查询（如「泵」）切不出 bigram，走 LIKE 兜底；顺序按 id 固定，可复算。
        if not rows:
            singles = [
                m.group(0)
                for m in _SEGMENT.finditer(query)
                if "\u4e00" <= m.group(0)[0] <= "\u9fff" and len(m.group(0)) == 1
            ]
            for char in singles[:3]:
                rows = conn.execute(
                    "SELECT id AS chunk_id, document_id, 0.0 AS score "
                    "FROM chunks WHERE content LIKE ? ORDER BY id LIMIT ?",
                    (f"%{char}%", top_k),
                ).fetchall()
                if rows:
                    break

        hits: list[dict[str, Any]] = []
        for i, row in enumerate(rows):
            payload = _hit_payload(conn, row, i + 1)
            if payload is not None:
                hits.append(payload)
        for i, hit in enumerate(hits):
            hit["no"] = i + 1

        return {
            "query": query,
            "tokenCount": len(tokens),
            "hitCount": len(hits),
            "hits": hits,
        }
    except sqlite3.Error as exc:
        # 不抛给 MCP 层：一次查询失败不该让整个工具进程退出，否则后面所有调用全断。
        return {"error": "query_failed", "detail": str(exc), "hits": []}
    finally:
        conn.close()


@server.tool(
    name="search_knowledge",
    description=(
        "检索本地企业资料库，返回与问题相关的原文片段。每条结果带 no（引用编号）、"
        "文件名、页码、片段序号和正文。只做检索，不生成答案；没有命中时返回空列表，"
        "调用方必须据此说明「资料里没有」，不得用常识补足。引用时写成 [no]。"
    ),
)
async def search_knowledge(query: str, top_k: int = 5) -> str:
    bounded = max(1, min(int(top_k), 20))
    # 检索是同步 sqlite 调用；放到线程池，避免堵住 MCP 的事件循环。
    result = await anyio.to_thread.run_sync(_search, query.strip(), bounded)
    return json.dumps(result, ensure_ascii=False)


async def _main() -> None:
    _log(f"启动，db={_db_path()}")
    await server.run_stdio_async()


if __name__ == "__main__":
    anyio.run(_main)

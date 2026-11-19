"""The shop's APIs in one file; the proxy sends each service only its own routes.

catalog: items from PostgreSQL, cached in Redis. orders: checks the item with catalog, then writes to PostgreSQL.
assistant: asks a small model on ollama.

orders has an "order history" feature, off by default, to demonstrate a memory leak: once turned on
(POST /api/orders/history), it keeps a receipt of every order in memory and never lets go of it, so the process
grows until the kernel's OOM killer stops it at the container's memory limit.
"""
import json
import os
import re
import time
import urllib.request
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import psycopg
import redis

DATABASE = dict(host="shop-db", dbname="shop", user="shop", password=os.environ["SHOP_DB_PASSWORD"],
                autocommit=True, connect_timeout=5)
CATALOG_URL = "http://catalog-api:8080"
OLLAMA_URL = "http://ollama:11434"
ROLE = os.environ["API_ROLE"]
MODEL = os.environ.get("OLLAMA_MODEL", "qwen3.5:9b")
RECEIPT_BYTES = int(os.environ.get("RECEIPT_KB", "3072")) * 1024
NAMES = ["Mug", "Notebook", "Pen", "Lamp", "Backpack", "Bottle", "Headphones", "Cable", "Charger", "Mouse",
         "Keyboard", "Sticker", "Poster", "Plant", "Candle", "Scarf", "Socks", "Cap", "Umbrella", "Wallet",
         "Puzzle", "Book", "Frame", "Clock"]

cache = redis.Redis(host="cache", socket_timeout=2, socket_connect_timeout=2)
ITEM = re.compile(r"^/api/catalog/items/(\d+)$")
history = None


def sql(statement, *params):
    with psycopg.connect(**DATABASE) as conn, conn.cursor() as cur:
        cur.execute(statement, params)
        return cur.fetchall() if cur.description else []


def call(method, url, body=None, timeout=5):
    data = json.dumps(body).encode() if body is not None else None
    request = urllib.request.Request(url, data, {"content-type": "application/json"}, method=method)
    with urllib.request.urlopen(request, timeout=timeout) as reply:
        return json.load(reply)


def item(item_id):
    key = f"item:{item_id}"
    cached = cache.get(key)
    if cached:
        return json.loads(cached)
    rows = sql("SELECT id, name, price FROM items WHERE id = %s", item_id)
    if not rows:
        return None
    found = {"id": rows[0][0], "name": rows[0][1], "price": float(rows[0][2])}
    cache.set(key, json.dumps(found), ex=20)
    return found


def route(method, path, body):
    global history
    if method == "GET" and path == "/api/catalog/items":
        return 200, [{"id": i, "name": n, "price": float(p)} for i, n, p in sql("SELECT id, name, price FROM items")]
    if method == "GET" and (match := ITEM.match(path)):
        found = item(int(match[1]))
        return (200, found) if found else (404, {"error": "no such item"})
    if method == "POST" and path == "/api/orders":
        found = call("GET", f"{CATALOG_URL}/api/catalog/items/{int(body['item'])}")
        qty = int(body.get("qty", 1))
        order_id = sql("INSERT INTO orders (item_id, qty, total) VALUES (%s, %s, %s) RETURNING id",
                       found["id"], qty, Decimal(str(found["price"])) * qty)[0][0]
        if history is not None:
            # The bug: every receipt is kept, and nothing ever removes one.
            history.append(f"receipt {order_id}\n".encode() * (RECEIPT_BYTES // 16))
        return 201, {"id": order_id}
    if method == "POST" and path == "/api/orders/history":
        if history is None:
            history = []
            print("order history turned on: keeping a receipt of every order", flush=True)
        return 200, {"history": "on"}
    if method == "GET" and path == "/api/orders":
        rows = sql("SELECT id, item_id, qty, total FROM orders ORDER BY id DESC LIMIT 10")
        return 200, [{"id": i, "item": it, "qty": q, "total": float(t)} for i, it, q, t in rows]
    if method == "POST" and path == "/api/assistant":
        # Without thinking: a thinking model would spend the whole token budget before it answers.
        reply = call("POST", f"{OLLAMA_URL}/api/generate", {
            "model": MODEL, "prompt": body.get("prompt", "Hello"), "stream": False, "think": False,
            "options": {"num_predict": 64},
        }, timeout=120)
        return 200, {"answer": reply.get("response", "")}
    return 404, {"error": "not found"}


class Handler(BaseHTTPRequestHandler):
    protocol_version = "HTTP/1.1"

    def handle_one(self, method):
        length = int(self.headers.get("content-length") or 0)
        try:
            body = json.loads(self.rfile.read(length)) if length else {}
            status, payload = route(method, self.path.split("?", 1)[0], body)
        except Exception as error:
            status, payload = 500, {"error": str(error)}
        data = json.dumps(payload).encode()
        self.send_response(status)
        self.send_header("content-type", "application/json")
        self.send_header("content-length", str(len(data)))
        self.end_headers()
        self.wfile.write(data)

    def do_GET(self):
        self.handle_one("GET")

    def do_POST(self):
        self.handle_one("POST")

    def log_message(self, *args):
        pass


def prepare():
    """catalog creates and seeds the tables; assistant pulls the model. Retries until the server is up."""
    while ROLE != "orders":
        try:
            if ROLE == "assistant":
                call("POST", f"{OLLAMA_URL}/api/pull", {"model": MODEL, "stream": False}, timeout=600)
            else:
                sql("""CREATE TABLE IF NOT EXISTS items (id int PRIMARY KEY, name text NOT NULL, price numeric(9, 2) NOT NULL);
                       CREATE TABLE IF NOT EXISTS orders (id serial PRIMARY KEY, item_id int NOT NULL, qty int NOT NULL,
                                                          total numeric(9, 2) NOT NULL, at timestamptz DEFAULT now())""")
                for i, name in enumerate(NAMES, 1):
                    sql("INSERT INTO items VALUES (%s, %s, %s) ON CONFLICT DO NOTHING",
                        i, name, Decimal(i * 37 % 50 + 4) + Decimal("0.99"))
            return
        except Exception as error:
            print(f"waiting: {error}", flush=True)
            time.sleep(3)


if __name__ == "__main__":
    prepare()
    ThreadingHTTPServer(("", 8080), Handler).serve_forever()

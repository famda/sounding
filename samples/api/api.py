"""The shop's APIs in one file; the gateway sends each service only its own routes.

catalog: items from SQL Server, cached in Redis. orders: checks the item with catalog, then writes to SQL Server.
assistant: asks a small model on ollama.
"""
import json
import os
import re
import time
import urllib.request
from decimal import Decimal
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer

import pytds
import redis

SQL = dict(server="sqlserver", user="sa", password=os.environ["SQL_PASSWORD"], autocommit=True, login_timeout=5)
CATALOG_URL = "http://catalog-api:8080"
OLLAMA_URL = "http://ollama:11434"
ROLE = os.environ["API_ROLE"]
MODEL = os.environ.get("OLLAMA_MODEL", "gemma3:270m")
NAMES = ["Mug", "Notebook", "Pen", "Lamp", "Backpack", "Bottle", "Headphones", "Cable", "Charger", "Mouse",
         "Keyboard", "Sticker", "Poster", "Plant", "Candle", "Scarf", "Socks", "Cap", "Umbrella", "Wallet",
         "Puzzle", "Book", "Frame", "Clock"]

cache = redis.Redis(host="cache", socket_timeout=2, socket_connect_timeout=2)
ITEM = re.compile(r"^/api/catalog/items/(\d+)$")


def sql(statement, *params, database="shop"):
    with pytds.connect(database=database, **SQL) as conn, conn.cursor() as cur:
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
    if method == "GET" and path == "/api/catalog/items":
        return 200, [{"id": i, "name": n, "price": float(p)} for i, n, p in sql("SELECT id, name, price FROM items")]
    if method == "GET" and (match := ITEM.match(path)):
        found = item(int(match[1]))
        return (200, found) if found else (404, {"error": "no such item"})
    if method == "POST" and path == "/api/orders":
        found = call("GET", f"{CATALOG_URL}/api/catalog/items/{int(body['item'])}")
        qty = int(body.get("qty", 1))
        order_id = sql("INSERT INTO orders (item_id, qty, total) OUTPUT INSERTED.id VALUES (%s, %s, %s)",
                       found["id"], qty, Decimal(str(found["price"])) * qty)[0][0]
        return 201, {"id": order_id}
    if method == "GET" and path == "/api/orders":
        rows = sql("SELECT TOP 10 id, item_id, qty, total FROM orders ORDER BY id DESC")
        return 200, [{"id": i, "item": it, "qty": q, "total": float(t)} for i, it, q, t in rows]
    if method == "POST" and path == "/api/assistant":
        reply = call("POST", f"{OLLAMA_URL}/api/generate", {
            "model": MODEL, "prompt": body.get("prompt", "Hello"), "stream": False, "options": {"num_predict": 64},
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
    """catalog creates and seeds the shop database; assistant pulls the model. Retries until the server is up."""
    while ROLE != "orders":
        try:
            if ROLE == "assistant":
                call("POST", f"{OLLAMA_URL}/api/pull", {"model": MODEL, "stream": False}, timeout=600)
            else:
                sql("IF DB_ID('shop') IS NULL CREATE DATABASE shop", database="master")
                sql("""IF OBJECT_ID('items') IS NULL BEGIN
                         CREATE TABLE items (id int PRIMARY KEY, name nvarchar(50) NOT NULL, price decimal(9, 2) NOT NULL)
                       END
                       IF OBJECT_ID('orders') IS NULL
                         CREATE TABLE orders (id int IDENTITY PRIMARY KEY, item_id int NOT NULL, qty int NOT NULL,
                                              total decimal(9, 2) NOT NULL, at datetime2 DEFAULT SYSUTCDATETIME())""")
                for i, name in enumerate(NAMES, 1):
                    sql("IF NOT EXISTS (SELECT 1 FROM items WHERE id = %s) INSERT INTO items VALUES (%s, %s, %s)",
                        i, i, name, Decimal(i * 37 % 50 + 4) + Decimal("0.99"))
            return
        except Exception as error:
            print(f"waiting: {error}", flush=True)
            time.sleep(3)


if __name__ == "__main__":
    prepare()
    ThreadingHTTPServer(("", 8080), Handler).serve_forever()

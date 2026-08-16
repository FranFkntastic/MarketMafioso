#!/usr/bin/env python3
"""Import MarketMafioso route listing books into Workshop Host without extracting gzip archives."""

from __future__ import annotations

import argparse
import csv
import gzip
import hashlib
import json
import re
import time
import urllib.error
import urllib.request
from collections import Counter, defaultdict
from datetime import datetime, timedelta
from pathlib import Path


def parse_args():
    parser = argparse.ArgumentParser()
    parser.add_argument("--root", required=True, type=Path, help="market-acquisition-route-logs directory")
    parser.add_argument("--endpoint", help="Workshop Host root, e.g. https://host/marketmafioso")
    parser.add_argument("--api-key-file", type=Path, help="file containing the ingest key")
    parser.add_argument("--dry-run", action="store_true", help="parse and report parity without uploading")
    return parser.parse_args()


def open_rows(path: Path):
    opener = gzip.open if path.suffix == ".gz" else open
    with opener(path, "rt", encoding="utf-8-sig", newline="") as stream:
        yield from csv.DictReader(stream)


def truth(value):
    return str(value).strip().lower() == "true"


def number(value, default=0):
    try:
        return int(value or default)
    except ValueError:
        return default


def elapsed(value):
    parts = (value or "0:0").split(":")
    if len(parts) == 3:
        hours, minutes, seconds = parts
    else:
        hours, (minutes, seconds) = "0", parts
    return timedelta(hours=int(hours), minutes=int(minutes), seconds=float(seconds))


def base_time(path: Path):
    manifest = path.parent / "manifest.json"
    if manifest.exists():
        value = json.loads(manifest.read_text(encoding="utf-8-sig"))["startedAtUtc"]
        return datetime.fromisoformat(value.replace("Z", "+00:00"))
    match = re.search(r"(\d{8})-(\d{6})", path.name)
    if not match:
        return datetime.fromtimestamp(path.stat().st_mtime).astimezone()
    return datetime.strptime("".join(match.groups()), "%Y%m%d%H%M%S").astimezone()


def snapshots(path: Path):
    base = base_time(path)
    current, previous_ordinal, current_signature = [], 0, None
    for row in open_rows(path):
        ordinal = number(row.get("rowOrdinal"))
        signature = tuple(row.get(key, "") for key in ("requestId", "currentWorld", "dataCenter", "lineId", "source", "itemId"))
        begins = bool(current) and (ordinal == 1 or signature != current_signature or (ordinal and ordinal <= previous_ordinal))
        if begins:
            yield build_snapshot(path, base, current)
            current = []
        current.append(row)
        current_signature, previous_ordinal = signature, ordinal
    if current:
        yield build_snapshot(path, base, current)


def build_snapshot(path: Path, base: datetime, rows):
    head = rows[0]
    listings, seen = [], set()
    fingerprint_rows = []
    for row in rows:
        listing_id = (row.get("listingId") or "").strip()
        if not listing_id or listing_id in seen:
            continue
        seen.add(listing_id)
        listing = {
            "listingId": listing_id,
            "retainerId": (row.get("retainerId") or "").strip(),
            "retainerName": (row.get("retainerName") or "").strip() or None,
            "quantity": number(row.get("quantity")),
            "unitPrice": number(row.get("unitPrice")),
            "isHq": truth(row.get("isHq")),
        }
        listings.append(listing)
        fingerprint_rows.append((listing_id, listing["retainerId"], listing["unitPrice"], listing["quantity"], listing["isHq"], row.get("decision", ""), row.get("reason", "")))
    truncated = truth(head.get("visibleListingCacheTruncated"))
    read_state = (head.get("listingReadState") or "").strip()
    declared = (head.get("coverageStatus") or "").strip()
    if not listings and read_state == "FreshComplete" and not truncated:
        coverage = "Empty"
    elif declared == "Complete" or (not declared and read_state == "FreshComplete" and not truncated):
        coverage = "Complete"
    elif declared == "Incomplete" or read_state == "FreshPartial" or truncated:
        coverage = "Partial"
    elif listings:
        coverage = "LegacyMissing"
    else:
        coverage = "Unavailable"
    observed = base + elapsed(head.get("elapsed"))
    book_fingerprint = hashlib.sha256(json.dumps(sorted(fingerprint_rows), separators=(",", ":")).encode()).hexdigest()
    identity = "|".join((head.get("currentWorld", ""), head.get("itemId", ""), book_fingerprint))
    return {
        "collapseKey": identity if listings else identity + "|" + observed.isoformat(),
        "bookFingerprint": book_fingerprint,
        "request": {
            "schemaVersion": 1,
            "idempotencyKey": "legacy:" + hashlib.sha256((file_hash(path) + "|" + identity + "|" + observed.isoformat()).encode()).hexdigest(),
            "occurrenceId": "legacy-" + hashlib.sha256((identity + "|" + observed.isoformat()).encode()).hexdigest(),
            "sourceKind": "LegacyRouteImport",
            "sourceVersion": "observed-listings-csv-v1",
            "sourceBuild": None,
            "captureMode": "LegacyUnknown",
            "itemId": number(head.get("itemId")),
            "itemName": (head.get("itemName") or "").strip() or None,
            "dataCenter": (head.get("dataCenter") or "").strip(),
            "worldName": (head.get("currentWorld") or head.get("listingWorld") or "").strip(),
            "observedAtUtc": observed.isoformat(),
            "coverage": coverage,
            "reportedListingCount": number(head.get("reportedListings"), len(listings)),
            "listingCapacity": number(head.get("listingCapacity")) or None,
            "isTruncated": truncated,
            "sourceFreshness": read_state or None,
            "provenanceJson": json.dumps({"artifactFingerprint": file_hash(path), "bookFingerprint": book_fingerprint}, separators=(",", ":")),
            "listings": listings,
        },
    }


_file_hashes = {}
def file_hash(path: Path):
    key = str(path)
    if key not in _file_hashes:
        digest = hashlib.sha256()
        with open(path, "rb") as stream:
            for block in iter(lambda: stream.read(1024 * 1024), b""):
                digest.update(block)
        _file_hashes[key] = digest.hexdigest()
    return _file_hashes[key]


def post(endpoint, api_key, relative, body):
    url = endpoint.rstrip("/") + "/api/market-intelligence/" + relative.lstrip("/")
    data = json.dumps(body, separators=(",", ":")).encode()
    headers = {"Content-Type": "application/json"}
    if api_key:
        headers["X-Api-Key"] = api_key
    request = urllib.request.Request(url, data=data, method="POST", headers=headers)
    for attempt in range(4):
        try:
            with urllib.request.urlopen(request, timeout=60) as response:
                return response.read()
        except urllib.error.HTTPError as error:
            detail = error.read().decode("utf-8", errors="replace")
            if error.code < 500 and error.code not in (408, 429):
                raise RuntimeError(f"HTTP {error.code} importing evidence: {detail}") from error
            if attempt == 3:
                raise RuntimeError(f"HTTP {error.code} importing evidence after retries: {detail}") from error
            time.sleep(2 ** attempt)
        except urllib.error.URLError:
            if attempt == 3:
                raise
            time.sleep(2 ** attempt)


def measures(request):
    listings = request["listings"]
    quantity = sum(row["quantity"] for row in listings)
    sellers = Counter(row["retainerId"] for row in listings)
    shelves = Counter()
    for row in listings:
        shelves[row["unitPrice"]] += row["quantity"]
    full = sum(row["quantity"] == 99 for row in listings) / len(listings) if listings else 0
    top_two = sum(value for _, value in sellers.most_common(2)) / len(listings) if listings else 0
    shelf = max(shelves.values(), default=0) / quantity if quantity else 0
    deep = len(listings) >= 80 and shelf >= .40
    return deep, deep and full >= .80 and top_two >= .35


def main():
    args = parse_args()
    root = args.root.resolve()
    paths = sorted(root.rglob("observed-listings.csv")) + sorted(root.glob("observed-listings-*.csv.gz"))
    if not paths:
        raise SystemExit("No observed-listings inputs were found.")
    if not args.dry_run and not args.endpoint:
        raise SystemExit("--endpoint is required unless --dry-run is used.")
    api_key = args.api_key_file.read_text(encoding="utf-8-sig").strip() if args.api_key_file else ""
    unique, imported_by_path, quarantined = {}, defaultdict(int), []
    for path in paths:
        try:
            for snapshot in snapshots(path):
                if snapshot["collapseKey"] not in unique:
                    unique[snapshot["collapseKey"]] = (path, snapshot["request"])
                    imported_by_path[path] += 1
        except Exception as error:
            quarantined.append((path, str(error)))
    deep = bulk = 0
    for _, request in unique.values():
        is_deep, is_bulk = measures(request)
        deep += is_deep
        bulk += is_bulk
    listing_books = sum(bool(request["listings"]) for _, request in unique.values())
    print(f"inputs={len(paths)} observations={len(unique)} listing_books={listing_books} deep_states={deep} bulk_states={bulk} quarantined={len(quarantined)}")
    if args.dry_run:
        return 0 if (deep, bulk) == (43, 24) and not quarantined else 2
    for index, (path, request) in enumerate(unique.values(), 1):
        post(args.endpoint, api_key, "evidence?deferProjection=true", request)
        if index % 100 == 0:
            print(f"uploaded={index}/{len(unique)}")
    for path in paths:
        relative_hash = hashlib.sha256(str(path.relative_to(root)).replace("\\", "/").encode()).hexdigest()
        failure = next((error for failed, error in quarantined if failed == path), None)
        post(args.endpoint, api_key, "import-receipts", {
            "sourcePathHash": relative_hash,
            "sourceFingerprint": file_hash(path),
            "status": "Quarantined" if failure else "Imported",
            "importedObservations": imported_by_path[path],
            "error": failure,
        })
    post(args.endpoint, api_key, "rebuild", {})
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

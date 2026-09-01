import sqlite3
import sys


path = sys.argv[1]
connection = sqlite3.connect(f"file:{path}?mode=ro", uri=True)
connection.execute("PRAGMA query_only = ON")

print("integrity_check", connection.execute("PRAGMA integrity_check").fetchone()[0])
foreign_keys = connection.execute("PRAGMA foreign_key_check").fetchall()
print("foreign_key_check", len(foreign_keys))
for pragma in ("page_size", "page_count", "freelist_count", "user_version", "application_id"):
    print(pragma, connection.execute(f"PRAGMA {pragma}").fetchone()[0])

print("\ndbstat")
for row in connection.execute(
    "SELECT name, sum(pgsize), count(*) FROM dbstat "
    "GROUP BY name ORDER BY sum(pgsize) DESC, name"
):
    print(*row, sep="\t")

print("\ntext_metrics")
tables = connection.execute(
    "SELECT name FROM sqlite_schema WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name"
).fetchall()
for (table,) in tables:
    quoted_table = '"' + table.replace('"', '""') + '"'
    for column in connection.execute(f"PRAGMA table_info({quoted_table})"):
        name = column[1]
        declared_type = column[2].upper()
        if declared_type != "TEXT":
            continue
        quoted_name = '"' + name.replace('"', '""') + '"'
        sql = (
            f"SELECT count(*), count({quoted_name}), count(DISTINCT {quoted_name}), "
            f"coalesce(sum(length(CAST({quoted_name} AS BLOB))), 0), "
            f"coalesce(avg(length(CAST({quoted_name} AS BLOB))), 0) FROM {quoted_table}"
        )
        values = connection.execute(sql).fetchone()
        print(table, name, *values, sep="\t")

if connection.execute(
    "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='AchPaymentRelatedInformation'"
).fetchone():
    print("\nach_addenda_samples")
    for (value,) in connection.execute(
        "SELECT Information FROM AchPaymentRelatedInformation "
        "WHERE Information LIKE 'TRN*%' LIMIT 10"
    ):
        print(value)
    for (value,) in connection.execute(
        "SELECT Information FROM AchPaymentRelatedInformation WHERE Information LIKE 'TXP*%'"
    ):
        print(value)

if connection.execute(
    "SELECT 1 FROM sqlite_schema WHERE type='table' AND name='AchTransactions'"
).fetchone() and any(x[1] == "RawPaymentRelatedInformation" for x in connection.execute("PRAGMA table_info(AchTransactions)")):
    print("\nraw_addenda")
    for (value,) in connection.execute(
        "SELECT RawPaymentRelatedInformation FROM AchTransactions WHERE RawPaymentRelatedInformation IS NOT NULL"
    ):
        print(repr(value))

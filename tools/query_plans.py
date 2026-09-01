import sqlite3
import sys


connection = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
mode = sys.argv[2]

if mode == "old":
    queries = {"ach": """
SELECT t.Id FROM Transactions t
JOIN DepositTransactions d ON d.TransactionId=t.Id
JOIN AchTransactions a ON a.TransactionId=t.Id
WHERE t.AccountLast4='8936' AND t.PostingDateUnixSeconds=1788134400
  AND t.AmountCents=1 AND t.TypeCode='ACH_CREDIT' AND d.DetailsCode='CREDIT'
"""}
else:
    common = "t.AccountId=2 AND t.PostingDay=20696 AND t.AmountCents=1 AND t.TypeId=1"
    queries = {
        "credit-card": f"SELECT t.Id FROM Transactions t JOIN CreditCardTransactions x ON x.TransactionId=t.Id WHERE {common} AND x.MerchantId=1",
        "ach": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN AchTransactions x ON x.TransactionId=t.Id WHERE {common} AND x.ProfileId=1",
        "transfer": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN AccountTransfers x ON x.TransactionId=t.Id WHERE {common} AND x.CounterpartyId=1",
        "card-payment": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN ChaseCardPayments x ON x.TransactionId=t.Id WHERE {common} AND x.TargetAccountId=3",
        "debit-card": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN DebitCardTransactions x ON x.TransactionId=t.Id WHERE {common} AND x.MerchantId=1",
        "atm": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN AtmTransactions x ON x.TransactionId=t.Id WHERE {common} AND x.ActionId=1",
        "fee": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN FeeTransactions x ON x.TransactionId=t.Id WHERE {common} AND x.Description='x'",
        "rtp": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN RealTimePayments x ON x.TransactionId=t.Id WHERE {common} AND x.Reference='x'",
        "unparsed": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id JOIN UnparsedDepositDescriptions x ON x.TransactionId=t.Id WHERE {common} AND x.Description='x'",
        "base-only": f"SELECT t.Id FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id WHERE {common} AND NOT EXISTS(SELECT 1 FROM AchTransactions x WHERE x.TransactionId=t.Id)",
        "accounting-view": "SELECT * FROM vTransactions WHERE AccountLast4='8936' AND PostingDay BETWEEN 20600 AND 20700 ORDER BY PostingDay",
        "reverse-provenance": "SELECT * FROM ImportRows WHERE TransactionId=1",
    }

for name, query in queries.items():
    print(name)
    for row in connection.execute("EXPLAIN QUERY PLAN " + query):
        print("  " + row[3])

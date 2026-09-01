import collections
import sqlite3
import sys


old = sqlite3.connect(f"file:{sys.argv[1]}?mode=ro", uri=True)
new = sqlite3.connect(f"file:{sys.argv[2]}?mode=ro", uri=True)


def compare(name, old_sql, new_sql):
    left = collections.Counter(old.execute(old_sql).fetchall())
    right = collections.Counter(new.execute(new_sql).fetchall())
    if left != right:
        print(name, "FAILED", "old-only", sum((left-right).values()), "new-only", sum((right-left).values()))
        print("old sample", list((left-right).items())[:3])
        print("new sample", list((right-left).items())[:3])
        raise SystemExit(1)
    print(name, "ok", sum(left.values()))


compare("transactions", """
SELECT AccountLast4, PostingDateUnixSeconds/86400, AmountCents, TypeCode FROM Transactions
""", """
SELECT AccountLast4, PostingDay, AmountCents, TypeCode FROM vTransactions
""")
compare("deposits", """
SELECT t.AccountLast4,t.PostingDateUnixSeconds/86400,t.AmountCents,t.TypeCode,
       d.DetailsCode,d.BalanceCents,d.CheckOrSlipNumber
FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id
""", """
SELECT AccountLast4,PostingDay,AmountCents,TypeCode,DetailsCode,BalanceCents,CheckOrSlipNumber
FROM vDepositTransactions
""")
compare("credit_cards", """
SELECT t.AccountLast4,t.PostingDateUnixSeconds/86400,t.AmountCents,t.TypeCode,
       c.TransactionDateUnixSeconds/86400,c.MerchantName,c.CategoryName,c.Memo
FROM Transactions t JOIN CreditCardTransactions c ON c.TransactionId=t.Id
""", """
SELECT v.AccountLast4,v.PostingDay,v.AmountCents,v.TypeCode,c.TransactionDay,
       m.Descriptor,cc.Name,c.Memo
FROM vTransactions v JOIN CreditCardTransactions c ON c.TransactionId=v.Id
JOIN MerchantDescriptors m ON m.Id=c.MerchantId
LEFT JOIN CreditCardCategories cc ON cc.Id=c.CategoryId
""")
compare("ach", """
SELECT t.AccountLast4,t.PostingDateUnixSeconds/86400,t.AmountCents,t.TypeCode,
 d.DetailsCode,d.BalanceCents,d.CheckOrSlipNumber,o.CompanyId,o.CompanyName,
 a.CompanyDescriptiveDate,ed.Description,sc.Code,tn.TraceNumber,
 a.EffectiveEntryDateUnixSeconds/86400,ii.IndividualId,nm.Name,pi.Information,
 brk.Name,br.Reference
FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id
JOIN AchTransactions a ON a.TransactionId=t.Id JOIN AchOriginators o ON o.Id=a.OriginatorId
JOIN AchEntryDescriptions ed ON ed.Id=a.EntryDescriptionId JOIN AchSecCodes sc ON sc.Id=a.SecCodeId
LEFT JOIN AchTraceNumbers tn ON tn.Id=a.TraceNumberId
LEFT JOIN AchIndividualIdentifiers ii ON ii.Id=a.IndividualIdentifierId
LEFT JOIN AchIndividualNames nm ON nm.Id=a.IndividualNameId
LEFT JOIN AchPaymentRelatedInformation pi ON pi.Id=a.PaymentRelatedInformationId
LEFT JOIN AchBankReferenceKinds brk ON brk.Id=a.BankReferenceKindId
LEFT JOIN AchBankReferences br ON br.Id=a.BankReferenceId
""", """
SELECT d.AccountLast4,d.PostingDay,d.AmountCents,d.TypeCode,d.DetailsCode,
 d.BalanceCents,d.CheckOrSlipNumber,o.CompanyIdentifier,co.Name,
 a.CompanyDescriptiveDate,ed.Description,sc.Code,a.TraceNumber,a.EffectiveEntryDay,
 a.IndividualIdentifier,a.IndividualName,
 coalesce(a.RawPaymentRelatedInformation,
   CASE WHEN tr.TransactionId IS NOT NULL THEN 'TRN*'||tr.TraceType||'*'||tr.Reference||'*'||tr.OriginatorIdentifier||CASE WHEN tr.AdditionalText IS NULL THEN '' ELSE '*'||tr.AdditionalText END||tr.Terminator
        WHEN tx.TransactionId IS NOT NULL THEN 'TXP*'||tx.TaxpayerId||'*'||tx.TaxType||'*'||tx.TaxPeriod||'*'||tx.AmountType||'*'||tx.AmountText||tx.Terminator END),
 brk.Name,a.BankReference
FROM vDepositTransactions d JOIN AchTransactions a ON a.TransactionId=d.Id
JOIN AchProfiles p ON p.Id=a.ProfileId JOIN AchOriginators o ON o.Id=p.OriginatorId
JOIN AchCompanies co ON co.Id=o.CompanyId JOIN AchEntryDescriptions ed ON ed.Id=p.EntryDescriptionId
JOIN AchSecCodes sc ON sc.Id=p.SecCodeId LEFT JOIN AchBankReferenceKinds brk ON brk.Id=p.BankReferenceKindId
LEFT JOIN AchTrnAddenda tr ON tr.TransactionId=a.TransactionId
LEFT JOIN AchTaxPaymentAddenda tx ON tx.TransactionId=a.TransactionId
""")
compare("transfers", """
SELECT t.AccountLast4,t.PostingDateUnixSeconds/86400,t.AmountCents,t.TypeCode,
 d.DetailsCode,d.BalanceCents,d.CheckOrSlipNumber,dir.Name,x.IsRealtime,
 c.Institution,c.AccountLabel,c.Last4,x.ChaseTransactionNumber,x.ChaseReference
FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id
JOIN AccountTransfers x ON x.TransactionId=t.Id JOIN TransferDirections dir ON dir.Id=x.DirectionId
JOIN TransferCounterparties c ON c.Id=x.CounterpartyId
""", """
SELECT AccountLast4,PostingDay,AmountCents,TypeCode,DetailsCode,BalanceCents,
 CheckOrSlipNumber,Direction,IsRealtime,Institution,AccountLabel,CounterpartyLast4,
 ChaseTransactionNumber,ChaseReference FROM vAccountTransfers
""")

base_old = """t.AccountLast4,t.PostingDateUnixSeconds/86400,t.AmountCents,t.TypeCode,
 d.DetailsCode,d.BalanceCents,d.CheckOrSlipNumber"""
base_new = """d.AccountLast4,d.PostingDay,d.AmountCents,d.TypeCode,
 d.DetailsCode,d.BalanceCents,d.CheckOrSlipNumber"""
join_old = """FROM Transactions t JOIN DepositTransactions d ON d.TransactionId=t.Id"""
join_new = """FROM vDepositTransactions d"""
compare("card_payments", f"""SELECT {base_old},p.TargetCardLast4 {join_old} JOIN ChaseCardPayments p ON p.TransactionId=t.Id""",
        f"""SELECT {base_new},printf('%04d',a.Last4) {join_new} JOIN ChaseCardPayments p ON p.TransactionId=d.Id JOIN Accounts a ON a.Id=p.TargetAccountId""")
compare("debit_cards", f"""SELECT {base_old},x.MerchantDescriptor {join_old} JOIN DebitCardTransactions x ON x.TransactionId=t.Id""",
        f"""SELECT {base_new},m.Descriptor {join_new} JOIN DebitCardTransactions x ON x.TransactionId=d.Id JOIN MerchantDescriptors m ON m.Id=x.MerchantId""")
compare("atms", f"""SELECT {base_old},x.Action,x.TerminalId,x.Location {join_old} JOIN AtmTransactions x ON x.TransactionId=t.Id""",
        f"""SELECT {base_new},CASE x.ActionId WHEN 1 THEN 'WITHDRAWAL' ELSE 'CASH_DEPOSIT' END,x.TerminalId,x.Location {join_new} JOIN AtmTransactions x ON x.TransactionId=d.Id""")
compare("fees", f"""SELECT {base_old},x.Description {join_old} JOIN FeeTransactions x ON x.TransactionId=t.Id""",
        f"""SELECT {base_new},x.Description {join_new} JOIN FeeTransactions x ON x.TransactionId=d.Id""")
compare("rtp", f"""SELECT {base_old},x.AbaRoutingNumber,x.Sender,x.Reference,x.OriginatorCompanyId,x.PaymentCode,x.Tin,x.Npi,x.ReceiverName,x.Purpose,x.InstructionId,x.ReceivedSecondOfDay,x.BankReference {join_old} JOIN RealTimePayments x ON x.TransactionId=t.Id""",
        f"""SELECT {base_new},printf('%09d',x.AbaRoutingNumber),s.Name,x.Reference,coalesce(o.CompanyIdentifier,x.RawOriginatorIdentifier),x.PaymentCode,x.Tin,x.Npi,x.ReceiverName,coalesce(e.Description,x.RawPurpose),x.InstructionId,x.ReceivedSecondOfDay,x.BankReference {join_new} JOIN RealTimePayments x ON x.TransactionId=d.Id JOIN RealTimePaymentSenders s ON s.Id=x.SenderId LEFT JOIN AchOriginators o ON o.Id=x.OriginatorId LEFT JOIN AchEntryDescriptions e ON e.Id=x.EntryDescriptionId""")
compare("unparsed", f"""SELECT {base_old},x.Description {join_old} JOIN UnparsedDepositDescriptions x ON x.TransactionId=t.Id""",
        f"""SELECT {base_new},x.Description {join_new} JOIN UnparsedDepositDescriptions x ON x.TransactionId=d.Id""")

-- Check how many tickets have been imported so far
SELECT COUNT(*) AS TicketsImported
FROM Ticket
WHERE StoreId = 9;  -- Garner store

-- Check the most recent tickets
SELECT TOP 10 
    FileNumber,
    TicketNumber,
    CreatedDate,
    DATEDIFF(SECOND, CreatedDate, GETDATE()) AS SecondsAgo
FROM Ticket
WHERE StoreId = 9
ORDER BY CreatedDate DESC;

-- Check if there are any blocking queries
SELECT 
    r.session_id,
    r.status,
    r.command,
    r.wait_type,
    r.wait_time,
    r.blocking_session_id,
    t.text AS query_text
FROM sys.dm_exec_requests r
CROSS APPLY sys.dm_exec_sql_text(r.sql_handle) t
WHERE r.database_id = DB_ID()
  AND r.session_id <> @@SPID;

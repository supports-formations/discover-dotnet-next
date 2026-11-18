#!/bin/bash
# Démarre SQL Server, puis restaure StarWarsTrade au premier lancement (si la base n'existe pas).
/opt/mssql/bin/sqlservr &
SQL=/opt/mssql-tools18/bin/sqlcmd
for i in $(seq 1 60); do
  $SQL -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -Q "SELECT 1" -b >/dev/null 2>&1 && break
  sleep 2
done
$SQL -C -S localhost -U sa -P "$MSSQL_SA_PASSWORD" -b -Q "
IF DB_ID('StarWarsTrade') IS NULL
  RESTORE DATABASE StarWarsTrade FROM DISK='/backup/StarWarsTrade.bak'
  WITH MOVE 'StarWarsTrade_data' TO '/var/opt/mssql/data/StarWarsTrade.mdf',
       MOVE 'StarWarsTrade_log'  TO '/var/opt/mssql/data/StarWarsTrade_log.ldf', STATS=10;"
echo ">>> StarWarsTrade prête"
wait

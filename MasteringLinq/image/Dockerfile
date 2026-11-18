FROM mcr.microsoft.com/mssql/server:2022-latest
LABEL org.opencontainers.image.source="https://github.com/supports-formations/discover-dotnet-next"
LABEL org.opencontainers.image.description="SQL Server 2022 + base StarWarsTrade (38 M de lignes) pour la formation MasteringLinq"
USER root
COPY StarWarsTrade.bak /backup/StarWarsTrade.bak
COPY entrypoint.sh /entrypoint.sh
RUN chmod +x /entrypoint.sh && chown -R mssql /backup
USER mssql
ENV ACCEPT_EULA=Y MSSQL_PID=Developer MSSQL_SA_PASSWORD="StarWars_Pwd123!"
EXPOSE 1433
ENTRYPOINT ["/entrypoint.sh"]

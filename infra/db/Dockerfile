FROM postgres:18-bookworm
WORKDIR /opt/leasebook-dbadmin
COPY admin.sh azure-bootstrap.sql verify.sql ./
USER postgres
ENTRYPOINT ["/bin/bash", "/opt/leasebook-dbadmin/admin.sh"]
CMD ["refuse"]

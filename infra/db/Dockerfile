FROM postgres:18-bookworm@sha256:3725f4e2499eef5134592b3b4ab79a543ed7f8e533b05b5b637af926630f6650
WORKDIR /opt/leasebook-dbadmin
COPY admin.sh azure-bootstrap.sql verify.sql ./
USER postgres
ENTRYPOINT ["/bin/bash", "/opt/leasebook-dbadmin/admin.sh"]
CMD ["refuse"]

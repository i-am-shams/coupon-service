The service connection is scoped to the subscription rather than a resource group. Azure DevOps recommends the narrower scope, and in a long-lived environment that's right — but the pipeline creates its own resource group as its first action, so a group-scoped connection would require someone to create it manually first, which contradicts the no-manual-steps requirement. In a real environment I'd scope the connection to a pre-provisioned group and treat that group as part of the platform, not the workload.
Commits are structured by phase rather than squashed, so the sequence of work is visible.
## Service connection
Workload identity federation with OpenID Connect, not a service principal secret.
Nothing to store or rotate; Azure DevOps and Entra trust each other directly and the
pipeline receives a short-lived token per run.

Scoped to the subscription rather than a resource group. Azure DevOps recommends the
narrower scope, but the pipeline creates its own resource group as its first action,
so a group-scoped connection would need someone to create that group by hand first —
which contradicts the no-manual-steps requirement.

Verified 2026-08-26: pipeline created and deleted a resource group successfully.

## Spike 1 — passwordless SQL via managed identity

Verified 2026-08-26.

Created an Azure SQL server with Entra-only authentication (no SQL login exists),
an App Service with a system-assigned managed identity, and a contained database
user for that identity — without granting the SQL Server the Directory Readers role.

The usual route, `CREATE USER ... FROM EXTERNAL PROVIDER`, makes SQL look the
identity up in Entra ID and therefore needs Directory Readers, which requires a
tenant administrator. Supplying the SID directly avoids the lookup entirely.

    CREATE USER [appspike98843] WITH SID = 0x875F35AF9A3FB34BB8F9EA1DC1F2F687, TYPE = E;

Confirmed in `sys.database_principals`:

    name             type_desc       sid
    appspike98843    EXTERNAL_USER   0x875F35AF9A3FB34BB8F9EA1DC1F2F687

### The SID is the client ID, not the object ID

The identity has two GUIDs:

- object ID  86c1786f-7168-43b9-bc90-29eadea1af6d  — used for RBAC role assignments
- client ID  af355f87-3f9a-4bb3-b8f9-ea1dc1f2f687  — used for the SQL SID

SQL wants the client ID. Using the object ID creates a user that exists and then
fails to authenticate.

### Byte order

The GUID's first three fields are little-endian; the last eight bytes are not:

    af355f87 -> 875F35AF
    3f9a     -> 9A3F
    4bb3     -> B34B
    b8f9ea1dc1f2f687 -> unchanged

Derived with `[guid]::ToByteArray()`. String manipulation would produce a SID that
looks plausible and silently fails.

### Conclusion

Passwordless SQL is achievable inside a pipeline with no tenant-admin involvement.
The approach document's §6 stands. No fallback to SQL authentication needed.

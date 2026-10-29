# Upstream schema

`quartz-4.6.2-sqlserver.sql` is copied with trailing whitespace normalized from the Quartz.NET v4.6.2 release:

https://github.com/quartznet/quartznet/blob/v4.6.2/database/tables/tables_sqlServer.sql

Upstream SHA-256: `01e2bce9a085b0f77d1c904cab98cf9e6c4230453be13f2a466da43b2cbc3ce4`.

Normalized vendored SHA-256: `f10755083064ab1363bfe6887771f6cd346de7c2bf608f6083f9acd4b1868bae`. DDL is unchanged.

The accompanying upstream Apache-2.0 license is preserved in QUARTZ-LICENSE.txt. At runtime the benchmark replaces the database placeholder with its freshly generated database name and changes @DropDb to 0; it never installs this schema into an existing application database. The benchmark project does not change Runner's runtime dependencies.

# SPR-213 recovery test requirement

Recovery Slice A must activate an automated migration regression proving:

> When schema v8 is introduced, invalidating legacy Database/Extraction
> publications must not perform row-wise cascading deletion proportional to
> retained derived values before Discovery can start.

The regression must seed a large retained schema-v7 publication, start
Discovery through the production repository-initialization boundary, and prove
that invalidation performs bounded metadata work before the first Discovery
progress event. It must fail if invalidation traverses retained value rows or
grows work in proportion to those rows.

R0 intentionally changes no production schema or migration code.

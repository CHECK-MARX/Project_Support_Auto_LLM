# Synthetic quality baselines

This directory contains compact, reviewed baselines for the artificial sample
dataset only. Baselines contain configuration identities, aggregate quality
metrics, and input fingerprints. They must not contain document text, customer
data, absolute paths, or performance timing values.

Update a baseline only after reviewing an intentional retrieval-quality change.
The RAG Lab treats these files as read-only inputs and writes all generated
candidate and regression reports below `reports/generated/`.

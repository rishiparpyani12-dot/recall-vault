# End-to-end tests

`McpProcessTests` launches the real API on an ephemeral loopback port and the real MCP server as a child process. It negotiates MCP over stdio with the official C# SDK, checks all eight tools, exercises the complete memory lifecycle, and verifies rejected credentials.

Each run uses a unique temporary SQLite directory. Credentials stay in process memory, child processes are terminated during teardown, and the temporary data is deleted.

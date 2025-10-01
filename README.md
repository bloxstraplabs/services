# Bloxstrap.Services

Services providing metrics collection (and soon actual useful features) at https://services.bloxstraplabs.com.

## Dev environment

You'll need Docker. If debugging in Visual Studio, make sure you use the 'docker-compose' startup project and **not** the Bloxstrap.Services one.

For dependencies:
- InfluxDB dashboard can be accessed at http://localhost:8086
- Postgres is forwarded on port 5432

See docker-compose.yml for info on creds.

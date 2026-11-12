# Sample deployment template

Invented, so that `toolrunner build` works out of the box. Replace it with the real templates (BUILD_TEMPLATE_DIR).

- Every file here is copied into the new deployment with `{{PLACEHOLDERS}}` filled in. Compose's own `${VARIABLES}` are left alone.
- `template.json` is not copied. It says which services are Keycloak clients and where they answer, which plugin
  packages each loads, the Keycloak roles and users, and how the finished stack is tested.
- Values always available: PRODUCT_TITLE, product_title, VERSION, REGISTRY, HOST, REALM, ENTITIES, PRIVILEGES,
  {{PORT+n}}, {{SECRET:name}}, {{PLUGINS:service}}, {{service.url}}, {{service.client}}, {{keycloak.url}},
  {{keycloak.admin}}, {{keycloak.adminPassword}}, {{user.ROLE.name}}, {{user.ROLE.password}}.

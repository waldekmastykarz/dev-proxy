# Dev Proxy Test Cases

This document outlines comprehensive test cases for each logical component of Dev Proxy and its plugin system, based on the codebase and configuration structure. Each test case is formulated as a desired state or behavior to be verified.

## Proxy Engine

- Initializes with valid configuration
- Initializes with URLs to watch
- Fails gracefully when no URLs to watch are configured
- Loads hostnames from URLs to watch
- Handles wildcards in URLs to watch
- Starts proxy server
- Listens on configured IP
- Listens on configured port
- Installs root certificate as required
- Loads root certificate as required
- Sets system proxy on macOS
- Sets system proxy on Windows
- Logs listening endpoints on startup
- Handles CONNECT requests for HTTPS tunneling
- Handles certificate validation events
- Handles certificate selection events
- Resets inactivity timer on each request
- Stops proxy on shutdown
- Unsubscribes from events on shutdown
- Supports interactive mode (hotkeys)
- Supports non-interactive mode (CI)
- Starts recording sessions as configured
- Stops recording sessions as configured
- Cleans up plugin data after each request
- Cleans up plugin data after each response
- Handles exceptions in plugins without crashing the proxy
- Adds proxy headers to outgoing requests
- Filters requests by headers if configured
- Only processes requests matching URLs to watch
- Handles requests with bodies
- Handles requests without bodies
- Handles responses with bodies
- Handles responses without bodies
- Supports global session data
- Supports per-plugin session data
- Logs intercepted requests with context
- Logs intercepted responses with context
- Supports multiple endpoints
- Logs each endpoint

## Plugin Loader

- Loads plugins from configuration file(s)
- Supports enabling plugins via config
- Supports disabling plugins via config
- Loads plugin assemblies from specified paths
- Binds plugin configuration sections correctly
- Handles missing plugin configuration gracefully
- Handles invalid plugin configuration gracefully
- Discovers available plugin options
- Discovers available plugin commands
- Removes duplicate options by name
- Registers plugin commands with the root command
- Supports dynamic plugin discovery (isDiscover mode)

## Plugin System (BasePlugin/BaseProxyPlugin)

- Initializes plugins with logger dependency
- Initializes plugins with context dependency
- Initializes plugins with URLs dependency
- Throws error if logger dependency is missing
- Throws error if context dependency is missing
- Throws error if URLs dependency is missing
- Validates plugin configuration on registration
- Logs configuration validation errors
- Supports plugin lifecycle event: InitializeAsync
- Supports plugin lifecycle event: RegisterAsync
- Supports plugin lifecycle event: BeforeRequestAsync
- Supports plugin lifecycle event: BeforeResponseAsync
- Supports plugin lifecycle event: AfterResponseAsync
- Supports plugin lifecycle event: AfterRequestLogAsync
- Supports plugin lifecycle event: AfterRecordingStopAsync
- Supports plugin lifecycle event: MockRequestAsync
- Allows plugins to store session data
- Allows plugins to retrieve session data
- Allows plugins to store global data
- Allows plugins to retrieve global data
- Supports plugin-specific options
- Supports plugin-specific commands
- Handles plugin disposal
- Handles plugin resource cleanup

## Configuration

- Loads configuration from the specified file (e.g., devproxyrc.json, m365.json)
- Supports schema validation for configuration files
- Supports per-environment configuration
- Supports per-profile configuration
- Loads URLs to watch from config
- Loads plugin list from config
- Supports plugin-specific configuration sections
- Handles missing configuration gracefully
- Handles malformed configuration gracefully
- Supports runtime configuration reload (if implemented)

## Certificate Management

- Loads root certificate on startup
- Creates root certificate on startup
- Stores certificate in configured location
- Sets certificate validity period to avoid browser errors
- Installs root certificate if required by config
- Loads certificate from environment variable path if set
- Handles certificate errors
- Logs certificate errors appropriately

## Request/Response Handling

- Intercepts HTTP requests matching URLs to watch
- Intercepts HTTPS requests matching URLs to watch
- Decrypts HTTPS traffic for matching hosts
- Preserves request bodies for plugin processing
- Preserves response bodies for plugin processing
- Handles requests with bodies
- Handles requests without bodies
- Handles responses with bodies
- Handles responses without bodies
- Supports modifying requests via plugins
- Supports modifying responses via plugins
- Supports custom headers
- Supports header filtering
- Handles tunnel (CONNECT) requests for HTTPS
- Supports logging of all intercepted traffic
- Supports tracing of all intercepted traffic

## System Proxy Integration

- Sets system proxy on macOS using shell script
- Sets system proxy on Windows using API
- Logs instructions for manual proxy setup on unsupported OS
- Handles proxy enable actions
- Handles proxy disable actions
- Cleans up system proxy settings on shutdown

## Plugins (General)

- Loads each enabled plugin
- Initializes each enabled plugin
- Passes correct configuration section to each plugin
- Handles plugin exceptions without affecting proxy
- Supports plugin-specific lifecycle events
- Allows plugins to modify requests
- Allows plugins to modify responses
- Supports plugin-specific session data
- Supports plugin-specific global data
- Allows plugins to register custom commands
- Allows plugins to register custom options
- Supports plugin disposal
- Supports plugin resource cleanup

## MockRequestPlugin

- Loads mock request configuration from file
- Initializes file watcher for mock file
- Prepares mock HTTP requests with correct method
- Prepares mock HTTP requests with correct URL
- Prepares mock HTTP requests with correct headers
- Prepares mock HTTP requests with correct body
- Handles string body types
- Handles JSON body types
- Adds content-type header as required
- Disposes HTTP client on cleanup
- Disposes loader on cleanup
- Handles missing mock configuration gracefully
- Handles invalid mock configuration gracefully

## DevToolsPlugin

- Initializes WebSocket server for DevTools integration
- Launches browser with correct debugging arguments
- Sends request events to DevTools frontend
- Sends response events to DevTools frontend
- Handles GetResponseBody requests from DevTools
- Manages request mapping for DevTools inspection
- Manages response mapping for DevTools inspection
- Handles browser process management
- Handles browser process cleanup
- Logs DevTools inspection URL
- Logs DevTools inspection status
- Handles WebSocket errors gracefully

## CrudApiPlugin

- Registers plugin only if base URL matches URLs to watch
- Loads API data
- Loads OpenID Connect configuration
- Handles authorization based on roles in token
- Handles authorization based on scopes in token
- Handles missing configuration gracefully
- Handles invalid configuration gracefully
- Registers BeforeRequest event handler

## AuthPlugin

- Sends JSON responses with correct headers
- Sends JSON responses with correct status codes
- Handles CORS headers if Origin is present in request
- Handles authentication logic
- Handles authorization logic
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## CachingGuidancePlugin

- Loads configuration from the correct section
- Warns when the same request is intercepted within the configured threshold
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## ExecutionSummaryPlugin

- Generates a summary report of requests after recording stops
- Groups activity by URL as configured
- Groups activity by message type as configured
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## GenericRandomErrorPlugin

- Loads error definitions from the configured file
- Fails requests with a random error from the list
- Applies failure rate as configured
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## HttpFileGeneratorPlugin

- Generates HTTP files from intercepted requests
- Generates HTTP files from intercepted responses
- Handles file output location as configured
- Handles file naming as configured
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## LatencyPlugin

- Delays responses by a random number of milliseconds within the configured range
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## MinimalCsomPermissionsPlugin

- Detects minimal permissions for SharePoint CSOM requests
- Reports required permissions after recording
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## MinimalPermissionsPlugin

- Checks if app uses minimal permissions for APIs using local API info
- Reports required permissions after recording
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## MinimalPermissionsGuidancePlugin

- Compares JWT token permissions to required scopes for recorded requests
- Shows differences in permissions
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## MockGeneratorPlugin

- Generates mock configurations based on intercepted requests
- Handles output file creation
- Handles output file naming
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## MockResponsePlugin

- Simulates responses as configured
- Handles various response scenarios (success)
- Handles various response scenarios (error)
- Handles various response scenarios (custom payloads)
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## ODataPagingGuidancePlugin

- Warns when OData paging requests use URLs not previously returned
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## OpenAIMockResponsePlugin

- Simulates responses from Azure OpenAI using a local model
- Simulates responses from OpenAI using a local model
- Handles various prompt scenarios
- Handles various completion scenarios
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## OpenApiSpecGeneratorPlugin

- Generates OpenAPI specs from intercepted requests
- Generates OpenAPI specs from intercepted responses
- Handles file output as configured
- Ensures schema correctness
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## RateLimitingPlugin

- Simulates rate-limit behaviors as configured
- Applies rate limits
- Returns appropriate headers
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## RetryAfterPlugin

- Simulates Retry-After header after throttling a request
- Applies configured delay
- Applies configured error code
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## RewritePlugin

- Rewrites requests as configured (URL)
- Rewrites requests as configured (headers)
- Rewrites requests as configured (body)
- Handles multiple rewrite rules
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## TypeSpecGeneratorPlugin

- Generates TypeSpec files from intercepted requests
- Generates TypeSpec files from intercepted responses
- Handles file output as configured
- Ensures schema correctness
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## UrlDiscoveryPlugin

- Creates a list of URLs intercepted by the proxy
- Handles output file creation
- Handles output file updates
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## Reporters

### JsonReporter

- Generates reports in JSON format from reporting plugins
- Handles file output as configured
- Handles file naming as configured

### MarkdownReporter

- Generates reports in Markdown format from reporting plugins
- Handles file output as configured
- Handles file naming as configured

### PlainTextReporter

- Generates reports in plain-text format from reporting plugins
- Handles file output as configured
- Handles file naming as configured

## Azure API Center Plugins

### ApiCenterMinimalPermissionsPlugin

- Checks minimal permissions for APIs using Azure API Center instance
- Reports required permissions after recording
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### ApiCenterOnboardingPlugin

- Checks if APIs used in app are registered in Azure API Center
- Reports onboarding status after recording
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### ApiCenterProductionVersionPlugin

- Checks if APIs used in app are production versions in Azure API Center
- Reports version status after recording
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## Microsoft Entra Plugins

### EntraMockResponsePlugin

- Mocks responses to Microsoft Entra APIs as configured
- Handles various response scenarios
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## Microsoft Graph Plugins

### GraphBetaSupportGuidancePlugin

- Warns when requests are made to Microsoft Graph beta endpoint
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphClientRequestIdGuidancePlugin

- Warns when requests to Microsoft Graph lack client-request-id header
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphConnectorGuidancePlugin

- Provides guidance for working with Microsoft Graph connectors
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphConnectorNotificationPlugin

- Simulates notifications for enabling Microsoft Graph connectors in Teams Admin Center
- Simulates notifications for disabling Microsoft Graph connectors in Teams Admin Center
- Validates requests for creating external connections
- Validates requests for deleting external connections
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphMinimalPermissionsPlugin

- Returns minimal permissions required for Microsoft Graph requests
- Supports delegated permissions
- Supports application permissions
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphMinimalPermissionsGuidancePlugin

- Compares JWT token permissions to required scopes for Microsoft Graph requests
- Shows differences in permissions
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphMockResponsePlugin

- Mocks responses to Microsoft Graph APIs as configured
- Handles various response scenarios
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphRandomErrorPlugin

- Fails requests to Microsoft Graph with random errors from allowed list
- Applies failure rate as configured
- Applies retry-after as configured
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphSdkGuidancePlugin

- Warns when requests to Microsoft Graph are not issued by Microsoft Graph SDK
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### GraphSelectGuidancePlugin

- Warns when requests to Microsoft Graph lack $select query parameter
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

### ODSPSearchGuidancePlugin

- Warns when requests are made to OneDrive search APIs
- Warns when requests are made to SharePoint search APIs
- Handles missing configuration gracefully
- Handles invalid configuration gracefully

## Logging

- Logs all significant startup events
- Logs all significant shutdown events
- Logs all significant errors
- Logs all significant requests
- Logs all significant responses
- Logs plugin-specific events
- Logs plugin-specific errors
- Supports configurable log levels
- Logs timestamps if enabled in config
- Logs new version notifications if enabled

## Error Handling

- Handles all exceptions gracefully
- Logs all exceptions
- Continues processing other requests after an error
- Continues processing other plugins after an error
- Validates configuration and logs errors
- Handles plugin errors without crashing proxy

## MCP Server (Proxy API) Integration

- Exposes a documented web API (Swagger UI available at `http://localhost:<apiPort>/swagger`)
- Returns proxy status via `GET /proxy`
- Returns configuration file path via `GET /proxy`
- Starts recording via `POST /proxy` with `{ "recording": true }`
- Stops recording via `POST /proxy` with `{ "recording": false }`
- Generates JWT tokens with custom claims via `POST /proxy/jwtToken`
- Generates JWT tokens with custom roles via `POST /proxy/jwtToken`
- Generates JWT tokens with custom scopes via `POST /proxy/jwtToken`
- Generates JWT tokens with custom validity via `POST /proxy/jwtToken`
- Raises a mock request via `POST /proxy/mockrequest`
- Downloads the root certificate in PEM format via `GET /proxy/rootCertificate?format=crt`
- Gracefully shuts down Dev Proxy via `POST /proxy/stopproxy`
- Returns correct HTTP status codes for invalid requests
- Returns correct error messages for invalid requests
- Returns correct HTTP status code for unsupported certificate format
- Ignores user-supplied values for registered JWT claims
- Uses system-generated values for registered JWT claims
- Handles concurrent API requests without race conditions
- Handles concurrent API requests without data loss
- Reflects real-time proxy state in API responses
- Reflects real-time recording status in API responses
- Handles malformed API requests gracefully
- Handles incomplete API requests gracefully
- API is available on the configured port
- API is secured as documented
- All API operations are covered by Swagger/OpenAPI documentation

---

This list is intended to be exhaustive and ultrahard, covering all logical and edge cases for Dev Proxy, its plugin system, and the MCP server (Proxy API). Each test case should be implemented and verified to ensure the robustness and reliability of the system.

## Additional Test Areas for Industry-Leading Coverage

### Performance & Scalability

- Handles at least 1000 concurrent requests per second with no failed requests (stress/load test)
- Handles at least 1000 concurrent requests per second with no increased response times (stress/load test)
- Memory usage remains stable (within 10% of baseline) after 1 hour of continuous operation under load
- Adding plugins increases average response time by no more than 10% compared to baseline
- Proxy startup time is less than 2 seconds under normal conditions
- Proxy shutdown completes in less than 1 second

### Backward/Forward Compatibility

- Loads configuration files from previous two major versions without errors
- Loads configuration files from previous two major versions without warnings
- Loads plugins built for previous two major versions
- All plugin features function as described in documentation for previous two major versions
- Schema migrations or version upgrades complete without data loss
- Schema migrations or version upgrades complete without errors
- All configuration values are preserved after schema migrations or version upgrades

### Upgrade & Installation

- Installation scripts (PowerShell) complete with exit code 0 on macOS
- Installation scripts (PowerShell) complete with exit code 0 on Windows
- Installation scripts (PowerShell) complete with exit code 0 on Linux
- Installation scripts (shell) complete with exit code 0 on macOS
- Installation scripts (shell) complete with exit code 0 on Windows
- Installation scripts (shell) complete with exit code 0 on Linux
- Installation scripts (Docker) complete with exit code 0 on macOS
- Installation scripts (Docker) complete with exit code 0 on Windows
- Installation scripts (Docker) complete with exit code 0 on Linux
- All files are present after installation on macOS
- All files are present after installation on Windows
- All files are present after installation on Linux
- Docker image builds without errors
- Docker image passes all documented health checks
- Docker image passes all documented smoke tests
- Upgrading from previous two major versions preserves all configuration files
- Upgrading from previous two major versions preserves all user data
- Uninstallation removes all installed files
- Uninstallation reverts system proxy settings
- Uninstallation reverts other changes

### Internationalization/Localization

- Non-ASCII/Unicode characters in configuration are preserved in all outputs
- Non-ASCII/Unicode characters in requests are preserved in all outputs
- Non-ASCII/Unicode characters in responses are preserved in all outputs
- Non-ASCII/Unicode characters in logs are preserved in all outputs
- All APIs accept UTF-8 encoded data
- All APIs return UTF-8 encoded data
- All plugins accept UTF-8 encoded data
- All plugins return UTF-8 encoded data
- Round-trip tests with Unicode payloads succeed

### Advanced Security

- Requests with revoked certificates are rejected with appropriate error codes
- Requests with expired certificates are rejected with appropriate error codes
- Requests with invalid certificates are rejected with appropriate error codes
- Requests with revoked certificates are rejected with appropriate error messages
- Requests with expired certificates are rejected with appropriate error messages
- Requests with invalid certificates are rejected with appropriate error messages
- Replayed JWT tokens are rejected with HTTP 401
- Replayed JWT tokens are rejected with HTTP 403
- Tampered JWT tokens are rejected with HTTP 401
- Tampered JWT tokens are rejected with HTTP 403
- API endpoints are tested with injection payloads and do not execute malicious input
- API endpoints are tested with XSS payloads and do not reflect malicious input
- API endpoints are tested with CSRF payloads and do not execute malicious input
- Permission errors result in HTTP 401
- Permission errors result in HTTP 403
- Authentication errors result in HTTP 401
- Authentication errors result in HTTP 403
- No sensitive data is leaked in the response for permission or authentication errors

### User Experience (UX) & CLI

- CLI help output matches documented examples
- CLI usage output matches documented examples
- CLI help output includes all available commands
- CLI help output includes all available options
- Error messages in the console include actionable guidance
- Error messages in the console reference documentation or next steps
- Switching between interactive and non-interactive modes does not result in loss of state
- Switching between interactive and non-interactive modes does not result in errors
- All features remain available when switching modes
- All hotkey options are listed in documentation
- All command-line options are listed in documentation
- All hotkey options function as described in tests
- All command-line options function as described in tests

### Plugin Interactions

- Multiple plugins modifying the same request do not conflict as verified by integration tests
- Multiple plugins modifying the same response do not conflict as verified by integration tests
- Multiple plugins modifying the same request do not cause errors as verified by integration tests
- Multiple plugins modifying the same response do not cause errors as verified by integration tests
- Plugin execution order is deterministic and matches the order specified in configuration
- Plugin execution order is deterministic and matches the order specified in documentation
- Disabling plugins at runtime results in correct plugin behavior as verified by test
- Enabling plugins at runtime results in correct plugin behavior as verified by test

### Telemetry & Analytics (if applicable)

- Telemetry is only collected when user consent is given via configuration
- Telemetry is only collected when user consent is given via command-line flag
- Telemetry data matches expected values for triggered events
- Telemetry data contains no personally identifiable information
- Telemetry data is described in documentation
- Opt-in for telemetry is controlled by a documented configuration setting
- Opt-in for telemetry is controlled by a documented command-line flag
- Opt-out for telemetry is controlled by a documented configuration setting
- Opt-out for telemetry is controlled by a documented command-line flag
- Enabling telemetry results in telemetry being sent as verified by network inspection
- Disabling telemetry results in telemetry not being sent as verified by network inspection

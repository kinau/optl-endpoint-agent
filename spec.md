# Project
Use Open Telemetry SDK to create a client that collect telemetry and report to Open Telemetry Collector backends. Metrics backend is Prometheus service, Traces Backend is Grafana Tempo service, Logs Backend is Grafana Loki service, Visualize metrics, traces and logs in Grafana.

# Use Cases
- As SRE, I want to monitor endpoints performance in terms of device performance, app performance, user activities at the endpoint and visual it in a dashboard. 

# Requirements
- As a data collection agent, collect and report telemetry to Open Telemetry Collector backends. 

- Open Telemetry Collector backends has pipeline to process metrics to Prometheus and visualize in Grafana.

- Open Telemery Collector backends has pipeline to process traces to Grafana Tempo.

- Open Telemery Collector backends has pipeline to process logs to Grafana Loki.

- Data collection agent should target Windows, macOS platforms.

# Design
- Create a data collection agent using .NET 10 and  Open Telemetry SDK. It performs data collection that coverage data points by uberAgent documented here https://docs.citrix.com/en-us/uberagent/7-4-0/metrics  

- The data collection agent reports to Open Telemetry Collector backends

- The data collection agent reports every 1 minute.

- Create an instance of Open Telemetry Collector in Docker

- Create an instance of Promotheus in Docker

- Create an instance of Grafana Tempo in Docker

- Create an instance of Grafana Loki in Docker

- Create an instance of Grafana for visualization

- Create a pipeline in Open Telemetry Collector to export metrics to Promotheus

- Create a pipeline in Open Telemetry Collector to export traces to Grafana Tempo

- Create a pipeline in Open Telemetry Collector to export logs to Grafana Loki

- Create a Grafana dashboard to visual metrics report by data collection agent


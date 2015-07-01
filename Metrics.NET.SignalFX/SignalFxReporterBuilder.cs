﻿
using Metrics.Logging;
using Metrics.Reporters;
using Metrics.SignalFx.Configuration;
using Metrics.SignalFx.Helpers;
using System;
using System.Collections.Generic;
using System.IO;

namespace Metrics.SignalFx
{
    /// <summary>
    /// A Builder used for end-user Extension methods in setting up the SignalFuse reporting mechanisms
    /// </summary>
    public class SignalFxReporterBuilder
    {
        private static readonly ILog log = LogProvider.GetCurrentClassLogger();
        private static readonly string DEFAULT_URI = "https://ingest.signalfx.com";
        private static readonly int MAX_DATAPOINTS_PER_MESSAGE = 10000;
        private static readonly string INSTANCE_ID_DIMENSION = "InstanceId";

        private string apiToken;
        private TimeSpan interval;
        private IDictionary<string, string> defaultDimensions = new Dictionary<string, string>();
        private string baseURI = DEFAULT_URI;
        private int maxDatapointsPerMessage = MAX_DATAPOINTS_PER_MESSAGE;
        private string defaultSource;

        /// <summary>
        /// The hidden internal constructor
        /// </summary>
        internal SignalFxReporterBuilder(string apiToken, TimeSpan interval)
        {
            this.apiToken = apiToken;
            this.interval = interval;
        }

        /// <summary>
        /// Set up the default dimensions that go out with reports coming from reporters that the builder creates
        /// </summary>
        /// <param name="defaultDimensions">The dimensions that should go out with the reports</param>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithDefaultDimensions(IDictionary<string, string> defaultDimensions)
        {
            this.defaultDimensions = defaultDimensions;
            return this;
        }

        /// <summary>
        /// Set the base URI that the constructed reporter will send to
        /// </summary>
        /// <param name="baseURI">The base URI that the constructed reporter will send to</param>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithBaseURI(String baseURI)
        {
            this.baseURI = baseURI;
            return this;
        }

        /// <summary>
        /// Set the limit for the number of data points that can be contained in each message being reported
        /// </summary>
        /// <param name="maxDatapointsPerMessage">The maximum number of data points that can be in each message</param>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithMaxDatapointsPerMessage(int maxDatapointsPerMessage)
        {
            this.maxDatapointsPerMessage = maxDatapointsPerMessage;
            return this;
        }

        /// <summary>
        /// Tell the reporter to use the NetBios name as the source for reported messages
        /// </summary>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithNetBiosNameSource()
        {
            return WithSource(System.Environment.MachineName);
        }

        /// <summary>
        /// Tell the reporter to use the reverse lookup DNS name as the source for reported messages
        /// </summary>
        /// <remarks>
        /// Note that this requires that the DNS Servers as configured on the system under which the code 
        /// is running have valid PTR records for the system. Otherwise, it will simply fall back to the
        /// NetBIOS name of the system.
        /// </remarks>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithDNSSource()
        {
            return WithSource(System.Net.Dns.GetHostName());
        }

        /// <summary>
        /// Tell the reporter to use the FQDN per the Windows IP Helper API
        /// </summary>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithFQDNSource()
        {
            string domainName = System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties().DomainName;
            string hostName = System.Net.Dns.GetHostName();

            if (!hostName.EndsWith(domainName))  // if hostname does not already include domain name
            {
                hostName += "." + domainName;   // add the domain name part
            }
            return WithSource(hostName);
        }

        /// <summary>
        /// Tell the reporter to use magic AWS REST address to get the AWS instance Id
        /// </summary>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithAWSInstanceIdDimension(IWebRequestor awsRequestor = null)
        {
            if (awsRequestor == null)
            {
                awsRequestor = new WebRequestor("http://169.254.169.254/latest/meta-data/instance-id")
                    .WithTimeout(1000 * 60)
                    .WithMethod("GET");
            }
            using (var resp = awsRequestor.Send())
            {
                string source = new StreamReader(resp).ReadToEnd();
                defaultDimensions[INSTANCE_ID_DIMENSION] = source;
                return this;
            }
        }

        /// <summary>
        /// Set up the source that goes out with reports coming from reporters that the builder creates
        /// </summary>
        /// <param name="defaultSource">The source to use in reports</param>
        /// <returns>this</returns>
        public SignalFxReporterBuilder WithSource(string defaultSource)
        {
            this.defaultSource = defaultSource;
            return this;
        }

        /// <summary>
        /// Buidl the actual reporter
        /// </summary>
        /// <returns></returns>
        public Tuple<MetricsReport, TimeSpan> Build()
        {
            return new Tuple<MetricsReport, TimeSpan>(new SignalFxReport(new SignalFxReporter(baseURI, apiToken), defaultSource, defaultDimensions, maxDatapointsPerMessage), interval);
        }

        public static SignalFxReporterBuilder FromAppConfig()
        {
            try
            {
                SignalFxReporterConfiguration config = SignalFxReporterConfiguration.FromConfig();

                if (config == null)
                {
                    return null;
                }

                IDictionary<string, string> defaultDimensions = new Dictionary<string, string>();
                if (config.DefaultDimensions != null)
                {
                    foreach (DefaultDimension defaultDimension in config.DefaultDimensions)
                    {
                        defaultDimensions.Add(defaultDimension.Name, defaultDimension.Value);
                    }
                }
                SignalFxReporterBuilder builder = new SignalFxReporterBuilder(config.APIToken, config.SampleInterval);
                builder.WithBaseURI(config.BaseURI);
                builder.WithMaxDatapointsPerMessage(config.MaxDatapointsPerMessage);
                builder.WithDefaultDimensions(defaultDimensions);
                if (config.AwsIntegration)
                {
                    builder.WithAWSInstanceIdDimension();
                }
                switch (config.SourceType)
                {
                    case SourceType.netbios:
                        builder.WithNetBiosNameSource();
                        break;
                    case SourceType.dns:
                        builder.WithDNSSource();
                        break;
                    case SourceType.fqdn:
                        builder.WithFQDNSource();
                        break;
                    case SourceType.custom:
                        if (!string.IsNullOrEmpty(config.SourceValue))
                        {
                            builder.WithSource(config.SourceValue);
                            break;
                        }
                        throw new Exception("Metrics.SignalFx.Source.Value must be set if Metrics.SignalFx.Source.Type is \"source\".");
                    default:
                        throw new Exception("Metrics.SignalFx.Source.Type must be one of netbios, dns, fqdn, or source(with Metrics.SignalFx.Source.Value set)");
                }
                return builder;
            }
            catch (Exception x)
            {
                log.ErrorException("Metrics: Error configuring SignalFx reports", x);
                throw new InvalidOperationException("Invalid Metrics Configuration: Metrics.SignalFx.APIToken must be non-empty and Metrics.SignalFx.Interval.Seconds must be an integer > 0", x);

            }
        }
    }
}

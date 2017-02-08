using Com.Xceder.Messages;
using System;
using System.Linq;
using System.Reactive.Linq;
using System.Reactive.Subjects;

namespace Xceder
{
    /// <summary>
    /// provide the event stream from the Client
    /// </summary>
    public class XcederStream
    {
        /// <summary>
        /// hook the specified Client instance
        /// </summary>
        /// <param name="xcederClient"></param>
        public XcederStream(Client xcederClient)
        {
            XcederClient = xcederClient;

            ResponseStream = Observable.FromEventPattern<Response>(xcederClient, "ResponseEvent").Select(ev => { return ev.EventArgs; }).Publish();
            NetworkErrorStream = Observable.FromEventPattern<Exception>(xcederClient, "NetWorkErrorEvent").Select(ev => { return ev.EventArgs; }).Publish();

            ResponseStream.Connect();
            NetworkErrorStream.Connect();
        }

        /// <summary>
        /// create a Client instance and hook its event
        /// </summary>
        public XcederStream() : this(new Client())
        {

        }

        /// <summary>
        /// used Client instance for this stream 
        /// </summary>
        public Client XcederClient
        {
            get; private set;
        }

        /// <summary>
        /// return the recieve message event stream
        /// </summary>
        public IConnectableObservable<Response> ResponseStream
        {
            get; private set;
        }

        /// <summary>
        /// return the network error event stream
        /// </summary>
        public IConnectableObservable<Exception> NetworkErrorStream
        {
            get; private set;
        }
    }
}

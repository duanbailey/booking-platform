import type { LoaderFunctionArgs, RouteObject } from "react-router";
import { Link, useLoaderData } from "react-router";
import { session } from "@/entities/session";
import { allPages, pageQuery, type Business, type Page as ListPage, type Service } from "@/shared/api/pages";
import { formatMinutes } from "@/shared/lib/time";
import { Page } from "@/shared/ui/Page";
import styles from "./BusinessPage.module.css";

// T36: the business link owners send to customers (/b/{businessId}): its services, each linking to its slots.

interface BusinessData {
  business: Business;
  services: Service[];
}

async function businessLoader({ params, request }: LoaderFunctionArgs): Promise<BusinessData> {
  const id = encodeURIComponent(params.businessId ?? "");
  const signal = request.signal;
  const [business, services] = await Promise.all([
    session.authFetch<Business>(`/v1/businesses/${id}`, { signal }),
    allPages((cursor) => session.authFetch<ListPage<Service>>(`/v1/businesses/${id}/services${pageQuery(cursor)}`, { signal })),
  ]);
  return { business, services };
}

function BusinessPage() {
  const { business, services } = useLoaderData<BusinessData>();
  return (
    <Page title={business.name}>
      {services.length === 0
        ? <p>{business.name} has no services to book yet.</p>
        : (
          <ul className={styles.services}>
            {services.map((service) => (
              <li key={service.id}>
                <h2><Link to={`/b/${business.id}/services/${service.id}`}>{service.name}</Link></h2>
                {service.description && <p>{service.description}</p>}
                <p>Duration: {formatMinutes(service.durationMinutes)}</p>
                <p>Price: {service.price}</p>
              </li>
            ))}
          </ul>
        )}
    </Page>
  );
}

export const businessRoute: RouteObject = { path: "/b/:businessId", loader: businessLoader, element: <BusinessPage /> };

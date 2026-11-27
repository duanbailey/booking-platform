import { Link } from "react-router";
import { useSession } from "@/entities/session";
import { LogoutButton } from "@/features/logout";
import styles from "./AppHeader.module.css";

/** The header on every logged-in page: the app's name (to home), the user's name and Log out (T34). */
export function AppHeader() {
  const { user } = useSession();
  return (
    <header className={styles.header}>
      <Link to="/home" className={styles.name}>Booking platform</Link>
      <div className={styles.user}>
        <span>{user?.name}</span>
        <Link to="/profile">Profile</Link>
        <LogoutButton />
      </div>
    </header>
  );
}
